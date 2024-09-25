using System.Diagnostics;
using System.Xml.Serialization;

namespace JasperFx.Core
{
    public enum CopyBehavior
    {
        overwrite,
        preserve
    }

    public static class FileSystem 
    {
        public const int BufferSize = 32768;

        /// <summary>
        /// Create a directory if it does not already exist
        /// </summary>
        /// <param name="path"></param>
        public static void CreateDirectoryIfNotExists(string? path)
        {
            if (path.IsEmpty()) return;

            if (!path.Contains(Path.DirectorySeparatorChar) && !path.Contains(Path.AltDirectorySeparatorChar) && !path.Contains(Path.PathSeparator)) return;

            var dir = new DirectoryInfo(path);
            if (dir.Exists)
            {
                return;
            }

            dir.Create();
        }

        public static void Copy(string source, string destination)
        {
            Copy(source, destination, CopyBehavior.overwrite);
        }

        public static void Copy(string source, string destination, CopyBehavior behavior)
        {
            if (IsFile(source))
            {
                internalFileCopy(source, destination, behavior);
            }
            else
            {
                internalDirectoryCopy(source, destination, behavior);
            }
        }

        public static bool IsFile(string path)
        {
            //resolve the path
            path = Path.GetFullPath(path);

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                //TODO change back to IOException when it isn't defined in two assemblies anymore
                throw new Exception($"This path '{path}' doesn't exist!");
            }

            var attr = File.GetAttributes(path);

            return (attr & FileAttributes.Directory) != FileAttributes.Directory;
        }

        public static bool FileExists(string filename)
        {
            return File.Exists(filename);
        }

        public static void WriteStreamToFile(string filename, Stream stream)
        {
            CreateDirectoryIfNotExists(Path.GetDirectoryName(filename));

            var fileSize = 0;
            using (var fileStream = new FileStream(filename, FileMode.Create, FileAccess.Write))
            {
                int bytesRead;
                var buffer = new byte[BufferSize];
                do
                {
                    bytesRead = stream.Read(buffer, 0, buffer.Length);
                    fileSize += bytesRead;

                    if (bytesRead > 0)
                    {
                        fileStream.Write(buffer, 0, bytesRead);
                    }
                } while (bytesRead > 0);
                fileStream.Flush();
            }
        }

        public static void WriteStringToFile(string filename, string text)
        {
            CreateDirectoryIfNotExists(Path.GetDirectoryName(filename));

            File.WriteAllText(filename, text);
        }

        public static void AppendStringToFile(string filename, string text)
        {
            File.AppendAllText(filename, text);
        }


        public static void AlterFlatFile(string path, Action<List<string>> alteration)
        {
            var list = new List<string>();

            if (FileExists(path))
            {
                ReadTextFile(path, list.Add);
            }

            list.RemoveAll(x => x.Trim() == string.Empty);

            alteration(list);

            using(var fileStream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite))
            using (var writer = new StreamWriter(fileStream))
            {
                list.Each(x => writer.WriteLine(x));
            }
        }

        public static void DeleteDirectoryIfExists(string directory)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }

        public static void CleanDirectory(string directory)
        {
            if (directory.IsEmpty()) return;


            DeleteDirectoryIfExists(directory);
            Thread.Sleep(10);

            CreateDirectoryIfNotExists(directory);
        }

        public static void DeleteFileIfExists(string filename)
        {
            if (!File.Exists(filename)) return;

            File.Delete(filename);
        }

        public static void MoveFile(string from, string to)
        {
            CreateDirectoryIfNotExists(Path.GetDirectoryName(to));

            try
            {
                File.Move(from, to);
            }
            //TODO put this back to IOException when there is no longer a conflict
            catch (Exception ex)
            {
                var msg = $"Trying to move '{from}' to '{to}'";
                throw new Exception(msg, ex);
            }
        }

        public static void MoveFiles(string from, string to)
        {
            var files = Directory.GetFiles(from, "*.*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var partialPath = file.Replace(from, "");
                if (partialPath.StartsWith(@"\")) partialPath = partialPath.Substring(1);
                var newPath = Combine(to, partialPath);
                MoveFile(file, newPath);
            }
        }
        
        public static IEnumerable<string> FindFiles(string directory, FileSet searchSpecification)
        {
            var excluded = searchSpecification.ExcludedFilesFor(directory).ToArray();
            var files = searchSpecification.IncludedFilesFor(directory).ToList();
            files.RemoveAll(s => excluded.Contains(s));

            return files;
        }

        public static void ReadTextFile(string path, Action<string> callback)
        {
            if (!FileExists(path)) return;

            using(var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (var reader = new StreamReader(fileStream))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    callback(line.Trim());
                }
            }
        }

        private static void internalFileCopy(string source, string destination, CopyBehavior behavior)
        {
            var fileName = Path.GetFileName(source);

            var fullSourcePath = Path.GetFullPath(source);
            var fullDestPath = Path.GetFullPath(destination);


            var isFile = destinationIsFile(destination);

            var destinationDir = fullDestPath;
            if (isFile)
            {
                destinationDir = Path.GetDirectoryName(fullDestPath);
            }

            CreateDirectoryIfNotExists(destinationDir);

            if (!isFile) //aka its a directory
            {
                fullDestPath = Combine(fullDestPath, fileName);
            }

            var overwrite = behavior == CopyBehavior.overwrite;
            if (!overwrite && FileExists(fullDestPath)) return;

            try
            {
                File.Copy(fullSourcePath, fullDestPath, overwrite);
            }
            catch (Exception ex)
            {
                var msg = $"Was trying to copy '{fullSourcePath}' to '{fullDestPath}' and encountered an error. :(";
                throw new Exception(msg, ex);
            }
        }

        private static void internalDirectoryCopy(string source, string destination, CopyBehavior behavior)
        {
            var files = Directory.GetFiles(source, "*.*", SearchOption.AllDirectories);
            files.Each(file =>
            {
                var relative = file.PathRelativeTo(source);
                internalFileCopy(file, destination.AppendPath(relative), behavior);
            });
        }

        private static bool destinationIsFile(string destination)
        {
            if (FileExists(destination) || Directory.Exists(destination))
            {
                //it exists 
                return IsFile(destination);
            }

            if (destination.Last() == Path.DirectorySeparatorChar)
            {
                //last char is a '/' so its a directory
                return false;
            }

            //last char is not a '/' so its a file
            return true;
        }

        public static string Combine(params string[] paths)
        {
            return paths.Aggregate(Path.Combine);
        }

        public static void LaunchBrowser(string filename)
        {
            Process.Start("explorer", filename);
        }

        public static IEnumerable<string> GetChildDirectories(string directory)
        {
            if (!Directory.Exists(directory))
                return new string[0];


            return Directory.GetDirectories(directory);
        }
    }
}