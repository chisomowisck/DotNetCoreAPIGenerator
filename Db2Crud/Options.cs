namespace Db2Crud;

public sealed record Options(
    string Provider,
    string Conn,
    string Project,
    string ContextName,
    string Include,
    bool Verbose = false
);
