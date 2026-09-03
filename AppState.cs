using System;
using System.IO;

namespace Ferry
{
    internal sealed class AppState
    {
        private readonly object _gate = new object();
        private string _folderPath;

        public AppState(string initialPath)
        {
            _folderPath = ResolveInitialPath(initialPath);
        }

        public string FolderPath
        {
            get
            {
                lock (_gate)
                {
                    return _folderPath;
                }
            }
        }

        public FolderSnapshot ReadFolder()
        {
            return FolderCatalog.Inspect(FolderPath);
        }

        public FolderSnapshot SelectFolder(string path)
        {
            var snapshot = FolderCatalog.Inspect(path);
            lock (_gate)
            {
                _folderPath = snapshot.Path;
            }

            return snapshot;
        }

        private static string ResolveInitialPath(string requestedPath)
        {
            if (!string.IsNullOrWhiteSpace(requestedPath))
            {
                var requested = Path.GetFullPath(requestedPath);
                if (!Directory.Exists(requested))
                {
                    throw new DirectoryNotFoundException(string.Format("Folder not found: {0}", requested));
                }

                return requested;
            }

            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(documents) && Directory.Exists(documents))
            {
                return Path.GetFullPath(documents);
            }

            return Path.GetFullPath(Directory.GetCurrentDirectory());
        }
    }
}
