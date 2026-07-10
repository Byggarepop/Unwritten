namespace Unwritten.Core;

/// <summary>
/// A set of entities that changed together. In the git adapter this is a commit
/// (id = abbreviated SHA, label = subject line), but the core never assumes that.
/// </summary>
public sealed record Transaction(string Id, string Label, IReadOnlyCollection<string> Entities);
