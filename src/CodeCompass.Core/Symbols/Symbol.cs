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

/// <summary>A definition found by the syntactic layer: 1-based line/column of the name token.</summary>
public readonly record struct Symbol(string Name, SymbolKind Kind, string RelativePath, int Line, int Column);
