namespace CodeCompass.Core.Symbols;

public enum SymbolKind : byte
{
    Other = 0,
    Namespace,
    Class,
    Interface,
    Struct,
    Enum,
    Record,
    Method,
    Function,
    Property,
    Field,
    Trait,
    TypeAlias,
}

/// <summary>A definition found by the syntactic layer: 1-based line/column of the name token, plus the
/// 1-based end line of the enclosing declaration (so callers can show/read the definition's full span).
/// <see cref="EndLine"/> is 0 when unknown (e.g. an older index); treat that as "single line".</summary>
public readonly record struct Symbol(string Name, SymbolKind Kind, string RelativePath, int Line, int Column)
{
    /// <summary>1-based last line of the definition body (>= <see cref="Line"/>); 0 if unknown.</summary>
    public int EndLine { get; init; }
}
