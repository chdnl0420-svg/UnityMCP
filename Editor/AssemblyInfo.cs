using System.Runtime.CompilerServices;

// The bridge classes are internal because they are not an API anyone outside the package should call.
// The package's own tests do need to call them directly: driving a command through the file bridge
// would test the file watcher rather than the command.
[assembly: InternalsVisibleTo("ProjectMQaMcp.Editor.Tests")]
