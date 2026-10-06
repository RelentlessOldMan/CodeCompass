namespace CodeCompass.Core.Symbols;

/// <summary>
/// A tree-sitter grammar plus the query that extracts its definitions. NativeLibrary
/// and NativeFunction name the bundled grammar (e.g. "tree-sitter-c-sharp.dll" /
/// "tree_sitter_c_sharp"). Capture names in the query double as the symbol kind.
/// </summary>
public sealed class LanguageDefinition
{
    public string Key { get; }
    public string NativeLibrary { get; }
    public string NativeFunction { get; }
    public string QuerySource { get; }

    public LanguageDefinition(string key, string nativeLibrary, string nativeFunction, string querySource)
    {
        Key = key;
        NativeLibrary = nativeLibrary;
        NativeFunction = nativeFunction;
        QuerySource = querySource;
    }
}

/// <summary>Maps file extensions to grammars. MATLAB and anything unlisted stay lexical-only.</summary>
public static class LanguageRegistry
{
    private static readonly Dictionary<string, LanguageDefinition> ByExtension =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly List<LanguageDefinition> Definitions = new();

    static LanguageRegistry()
    {
        Register(CSharp, ".cs");
        Register(C, ".c");
        Register(Cpp, ".cpp", ".cc", ".cxx", ".c++", ".hpp", ".hh", ".hxx", ".h++", ".h", ".inl", ".ipp", ".tcc");
        Register(Python, ".py", ".pyi");
        Register(JavaScript, ".js", ".jsx", ".mjs", ".cjs");
        Register(TypeScript, ".ts");
        Register(Tsx, ".tsx");
        Register(Go, ".go");
        Register(Rust, ".rs");
        Register(T32, ".cmm");
    }

    private static void Register(LanguageDefinition def, params string[] extensions)
    {
        Definitions.Add(def);
        foreach (var ext in extensions)
            ByExtension[ext] = def;
    }

    public static LanguageDefinition? ForExtension(string ext) =>
        ByExtension.TryGetValue(ext, out var def) ? def : null;

    public static LanguageDefinition? ForPath(string path) =>
        ForExtension(Path.GetExtension(path));

    public static IReadOnlyList<LanguageDefinition> All => Definitions;

    // ---- grammar definitions -------------------------------------------------
    // Capture name = symbol kind (see SymbolKindMap). Queries stay conservative:
    // top-level definitions that are stable across grammar versions.

    private static readonly LanguageDefinition CSharp = new("csharp",
        "tree-sitter-c-sharp.dll", "tree_sitter_c_sharp", """
        (class_declaration name: (identifier) @class)
        (interface_declaration name: (identifier) @interface)
        (struct_declaration name: (identifier) @struct)
        (enum_declaration name: (identifier) @enum)
        (record_declaration name: (identifier) @record)
        (record_declaration (parameter_list (parameter name: (identifier) @property)))
        (method_declaration name: (identifier) @method)
        (constructor_declaration name: (identifier) @method)
        (property_declaration name: (identifier) @property)
        (namespace_declaration name: (_) @namespace)
        """);

    // C and C++ capture DEFINITIONS only: find_definition lists them and find_references (a C/C++ name search) skips their
    // positions. A type counts only when it has a body (`struct node *n` is a use, not a definition), and a plain function
    // only when it has a return type (`list_for_each(pos, head) { ... }` - a macro call followed by a block - parses as a
    // function definition without one). Pointer/reference-returning functions nest the name one or two declarators deeper.
    private static readonly LanguageDefinition C = new("c",
        "tree-sitter-c.dll", "tree_sitter_c", """
        (function_definition type: (_) declarator: (function_declarator declarator: (identifier) @function))
        (function_definition type: (_) declarator: (pointer_declarator declarator: (function_declarator declarator: (identifier) @function)))
        (function_definition type: (_) declarator: (pointer_declarator declarator: (pointer_declarator declarator: (function_declarator declarator: (identifier) @function))))
        (function_definition type: (_) declarator: (function_declarator declarator: (parenthesized_declarator (pointer_declarator declarator: (function_declarator declarator: (identifier) @function)))))
        (struct_specifier name: (type_identifier) @struct body: (_))
        (union_specifier name: (type_identifier) @struct body: (_))
        (enum_specifier name: (type_identifier) @enum body: (_))
        (function_definition type: (struct_specifier) declarator: (identifier) @struct)
        (function_definition type: (union_specifier) declarator: (identifier) @struct)
        (type_definition declarator: (type_identifier) @type)
        (type_definition declarator: (pointer_declarator declarator: (type_identifier) @type))
        (type_definition declarator: (function_declarator declarator: (parenthesized_declarator (pointer_declarator declarator: (type_identifier) @type))))
        """);

    // Function names can sit under pointer/reference declarators (`Value *f()`, `int &f()`) or return a function pointer
    // (`void (*f(int))(int)`), and members are named by a field_identifier (defined in the class body), a
    // qualified_identifier (`A::f`, `ns::A::f`), a destructor_name (`~A`) or an operator_name (`operator=`). Member shapes
    // need no return type (constructors have none, and a macro call never parses as a qualified or field name); a
    // constructor in the class body is a plain identifier with no return type, so it is matched only INSIDE a class body,
    // where a macro-call-plus-block doesn't occur. `class LIB_API Klass {...}` (an export macro) parses as a function named
    // Klass whose return type is the class specifier.
    private static readonly LanguageDefinition Cpp = new("cpp",
        "tree-sitter-cpp.dll", "tree_sitter_cpp", CppQuery());

    private static string CppQuery()
    {
        var wraps = new[]
        {
            "{0}",
            "(pointer_declarator declarator: {0})",
            "(reference_declarator {0})",
            "(pointer_declarator declarator: (pointer_declarator declarator: {0}))",
            "(function_declarator declarator: (parenthesized_declarator (pointer_declarator declarator: {0})))",
        };
        var sb = new System.Text.StringBuilder("""
            (class_specifier name: (type_identifier) @class body: (_))
            (struct_specifier name: (type_identifier) @struct body: (_))
            (union_specifier name: (type_identifier) @struct body: (_))
            (enum_specifier name: (type_identifier) @enum body: (_))
            (function_definition type: (class_specifier) declarator: (identifier) @class)
            (function_definition type: (struct_specifier) declarator: (identifier) @struct)
            (function_definition type: (union_specifier) declarator: (identifier) @struct)
            (namespace_definition name: (namespace_identifier) @namespace)
            (type_definition declarator: (type_identifier) @type)
            (type_definition declarator: (pointer_declarator declarator: (type_identifier) @type))
            (type_definition declarator: (function_declarator declarator: (parenthesized_declarator (pointer_declarator declarator: (type_identifier) @type))))
            (alias_declaration name: (type_identifier) @type)
            (field_declaration_list (function_definition !type declarator: (function_declarator declarator: (identifier) @method)))
            (field_declaration_list (template_declaration (function_definition !type declarator: (function_declarator declarator: (identifier) @method))))

            """);
        foreach (var w in wraps)
        {
            string Fn(string name) => string.Format(w, "(function_declarator declarator: " + name + ")");
            sb.AppendLine("(function_definition type: (_) declarator: " + Fn("(identifier) @function") + ")");
            foreach (var member in new[] { "(field_identifier) @method", "(destructor_name) @method", "(operator_name) @method" })
                sb.AppendLine("(function_definition declarator: " + Fn(member) + ")");
            foreach (var name in new[] { "(identifier) @method", "(destructor_name) @method", "(operator_name) @method" })
            {
                sb.AppendLine("(function_definition declarator: " + Fn("(qualified_identifier name: " + name + ")") + ")");
                sb.AppendLine("(function_definition declarator: " + Fn("(qualified_identifier name: (qualified_identifier name: " + name + "))") + ")");
            }
        }
        return sb.ToString();
    }

    private static readonly LanguageDefinition Python = new("python",
        "tree-sitter-python.dll", "tree_sitter_python", """
        (function_definition name: (identifier) @function)
        (class_definition name: (identifier) @class)
        """);

    private static readonly LanguageDefinition JavaScript = new("javascript",
        "tree-sitter-javascript.dll", "tree_sitter_javascript", """
        (function_declaration name: (identifier) @function)
        (class_declaration name: (identifier) @class)
        (method_definition name: (property_identifier) @method)
        """);

    private static readonly LanguageDefinition TypeScript = new("typescript",
        "tree-sitter-typescript.dll", "tree_sitter_typescript", """
        (function_declaration name: (identifier) @function)
        (class_declaration name: (type_identifier) @class)
        (interface_declaration name: (type_identifier) @interface)
        (enum_declaration name: (identifier) @enum)
        (method_definition name: (property_identifier) @method)
        """);

    private static readonly LanguageDefinition Tsx = new("tsx",
        "tree-sitter-tsx.dll", "tree_sitter_tsx", """
        (function_declaration name: (identifier) @function)
        (class_declaration name: (type_identifier) @class)
        (interface_declaration name: (type_identifier) @interface)
        (enum_declaration name: (identifier) @enum)
        (method_definition name: (property_identifier) @method)
        """);

    private static readonly LanguageDefinition Go = new("go",
        "tree-sitter-go.dll", "tree_sitter_go", """
        (function_declaration name: (identifier) @function)
        (method_declaration name: (field_identifier) @method)
        (type_declaration (type_spec name: (type_identifier) @type))
        """);

    private static readonly LanguageDefinition Rust = new("rust",
        "tree-sitter-rust.dll", "tree_sitter_rust", """
        (function_item name: (identifier) @function)
        (struct_item name: (type_identifier) @struct)
        (enum_item name: (type_identifier) @enum)
        (trait_item name: (type_identifier) @trait)
        (mod_item name: (identifier) @namespace)
        """);

    // Lauterbach TRACE32 PRACTICE scripts (.cmm). Grammar: codeberg.org/xasc/tree-sitter-t32 (MIT),
    // vendored + built to a native DLL (see grammars/). Captures mirror the grammar's tags.scm
    // definitions: a SUBROUTINE block and a labeled block are the two things you go-to-definition on.
    private static readonly LanguageDefinition T32 = new("t32",
        "tree-sitter-t32.dll", "tree_sitter_t32", """
        (subroutine_block subroutine: (identifier) @function)
        (labeled_expression label: (identifier) @label)
        """);
}
