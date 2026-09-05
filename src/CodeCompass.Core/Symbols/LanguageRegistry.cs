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
        Register(Cpp, ".cpp", ".cc", ".cxx", ".hpp", ".hh", ".hxx", ".h");
        Register(Python, ".py", ".pyi");
        Register(JavaScript, ".js", ".jsx", ".mjs", ".cjs");
        Register(TypeScript, ".ts");
        Register(Tsx, ".tsx");
        Register(Go, ".go");
        Register(Rust, ".rs");
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
        (method_declaration name: (identifier) @method)
        (constructor_declaration name: (identifier) @method)
        (property_declaration name: (identifier) @property)
        (namespace_declaration name: (_) @namespace)
        """);

    private static readonly LanguageDefinition C = new("c",
        "tree-sitter-c.dll", "tree_sitter_c", """
        (function_definition declarator: (function_declarator declarator: (identifier) @function))
        (struct_specifier name: (type_identifier) @struct)
        (union_specifier name: (type_identifier) @struct)
        (enum_specifier name: (type_identifier) @enum)
        (type_definition declarator: (type_identifier) @type)
        """);

    private static readonly LanguageDefinition Cpp = new("cpp",
        "tree-sitter-cpp.dll", "tree_sitter_cpp", """
        (class_specifier name: (type_identifier) @class)
        (struct_specifier name: (type_identifier) @struct)
        (enum_specifier name: (type_identifier) @enum)
        (namespace_definition name: (namespace_identifier) @namespace)
        (function_definition declarator: (function_declarator declarator: (identifier) @function))
        """);

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
}
