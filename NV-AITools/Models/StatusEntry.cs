namespace UvcsTools.Models;

sealed record StatusEntry(
    string Path,
    string StatusCode,
    string Status,
    string FullPath,
    bool IsDirectory);

sealed record StatusResult(
    int SchemaVersion,
    string WorkspaceRoot,
    IReadOnlyList<StatusEntry> Entries,
    IReadOnlyDictionary<string, int> StatusCounts);
