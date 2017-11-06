using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dropbox.Api;
using Dropbox.Api.Files;
using Microsoft.Extensions.Configuration;

namespace RetroPieBackup
{
    class Program
    {

        public static IConfigurationRoot Configuration { get; set; }

        static void Main(string[] args)
        {
            var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json");

            Task task = null;

            Configuration = builder.Build();
            //if (args.Count() > 0)
            //{
            //if (args.Contains("Download"))
            //{
            task = Task.Run((Func<Task>)Program.Run);
            //}
            //else if (args.Contains("Upload"))
            //{
            //    task = Task.Run((Func<Task>)Program.Download);
            //}

            task.Wait();
            //}
            //else
            //Console.WriteLine("Please, execute the program using Upload/Download parameter");

        }

        static async Task Run()
        {
            try
            {
                using (var dbx = new DropboxClient(Configuration["AuthToken"]))
                {
                    var full = await dbx.Users.GetCurrentAccountAsync();
                    Console.WriteLine($"{full.Name.DisplayName} - {full.Email}");
 
                    await FilesToUpload(dbx, Configuration["LocalSaveRootPath"]);
                    await FilesToDownload(dbx, Configuration["DropboxSavePath"]);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                Console.WriteLine(ex.InnerException);
                Console.ReadLine();
            }
        }

        /// <summary>
        /// Identify save files that are not up to date
        /// </summary>
        /// <returns>The to download.</returns>
        /// <param name="dbx">Dbx.</param>
        /// <param name="remotePath">Path.</param>
        static async Task FilesToDownload(DropboxClient dbx, string remotePath)
        {
            try
            {
                var list = await dbx.Files.ListFolderAsync(remotePath, false, true);

                foreach (var folder in list.Entries.Where(i => i.IsFolder))
                {
                    Console.WriteLine($"D  {remotePath}/{folder.Name}/");
                    await FilesToDownload(dbx, $"{remotePath}/{folder.Name}");
                }

                var filesToDownload = list.Entries.Where(i => i.IsFile).Count();
                var filesDownloaded = 0;

                Console.WriteLine($"{filesToDownload} Files found for {remotePath}");

                foreach (var file in list.Entries.Where(i => i.IsFile))
                {
                    var localFolder = $"{Configuration["LocalSaveRootPath"]}{remotePath}";

                    //Console.WriteLine($"{localFolder}/{file.Name}");

                    FileInfo fInfo = new FileInfo($"{localFolder}/{file.Name}");
                    if (fInfo == null || fInfo != null && IsFirstDateHigher(file.AsFile.ServerModified, fInfo.LastWriteTimeUtc))
                    {
                        await DownloadFile(dbx, remotePath, file.Name);
                        Console.WriteLine($"{++filesDownloaded}/{filesToDownload} files downloaded for {remotePath}.");
                    }
                    else{
                        Console.WriteLine($"{file.Name} - This file is up-to-date");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                Console.WriteLine(ex.InnerException);
            }
        }

        /// <summary>
        /// Downloads the file from Dropbox to local path.
        /// </summary>
        /// <returns>The file.</returns>
        /// <param name="dbx">Dbx.</param>
        /// <param name="folder">Folder.</param>
        /// <param name="fileName">File name.</param>
        static async Task DownloadFile(DropboxClient dbx, string folder, string fileName)
        {
            using (var response = await dbx.Files.DownloadAsync(folder + "/" + fileName))
            {
                //Console.WriteLine($"{Configuration["LocalSaveRootPath"]}{folder}/{fileName}");

                using (var stream = File.Create($"{Configuration["LocalSaveRootPath"]}{folder}/{fileName}"))
                {
                    var dataToWrite = await response.GetContentAsByteArrayAsync();
                    stream.Write(dataToWrite, 0, dataToWrite.Length);
                }
            }
        }

        /// <summary>
        /// Files to upload.
        /// </summary>
        /// <returns>The to upload.</returns>
        /// <param name="dbx">Dbx.</param>
        /// <param name="localPath">Local path.</param>
        static async Task FilesToUpload(DropboxClient dbx, string localPath)
        {
            var filePath = "";
            var filesUploaded = 0;
            var reg = new Regex(Configuration["SaveFiles"]);
            var allfiles = Directory.EnumerateFiles(localPath, "*.*", SearchOption.AllDirectories).Where(file => reg.IsMatch(file));
            var filesToBeUploaded = allfiles.Count();

            Console.WriteLine($"{filesToBeUploaded} Files found.");

            foreach (var file in allfiles)
            {
                filePath = file.Remove(0, localPath.Length);
                var dbxFile = await FileExists(dbx, filePath);
                //If file doesnt exists on Dropbox, upload it.
                if (dbxFile == null)
                {
                    await Upload(dbx, file, filePath);
                }
                else
                {
                    //If file is there, but your local version modified date is higher, upload it.
                    FileInfo fInfo = new FileInfo(file);
                    if (fInfo != null && IsFirstDateHigher(fInfo.LastWriteTime, dbxFile.AsFile.ServerModified))
                    {
                        await Upload(dbx, file, filePath);
                    }
                    else{
                        //In case your local version is behind, don't do anything.
                        Console.WriteLine($"{file} - Upload not needed.");
                        continue;
                    }
                }
                Console.WriteLine($"{++filesUploaded}/{filesToBeUploaded} files uploaded");
                Console.WriteLine(file);
            }
        }

        /// <summary>
        /// File exists.
        /// </summary>
        /// <returns>True if file exists.</returns>
        /// <param name="dbx">Dbx</param>
        /// <param name="path">Remote path to file</param>
        static async Task<Metadata> FileExists(DropboxClient dbx, string path)
        {
            try
            {
                return await dbx.Files.GetMetadataAsync(path, false, true);
            }
            catch (Exception ex)
            {
                if (!ex.Message.Contains("not_found"))
                    throw new Exception($"Uh oh, something bad happened. {ex.Message}");

                return null;
            }
        }

        /// <summary>
        /// Upload file to Dropbox
        /// </summary>
        /// <returns>The upload</returns>
        /// <param name="dbx">Dbx</param>
        /// <param name="localFilePath">Local file</param>
        /// <param name="remoteFilePath">File on the server</param>
        static async Task Upload(DropboxClient dbx, string localFilePath, string remoteFilePath)
        {
            using (var mem = new MemoryStream(File.ReadAllBytes(localFilePath)))
            {
                var updated = await dbx.Files.UploadAsync(remoteFilePath, WriteMode.Overwrite.Instance, body: mem);
                Console.WriteLine($"Saved {remoteFilePath} rev {updated.Rev}");
            }
        }

        /// <summary>
        /// Compare dates... ToUniversalTime() is used as I got inconsistent values when comparing localFile and RemoteFile dates.
        /// </summary>
        /// <returns><c>true</c>, if first date higher was ised, <c>false</c> otherwise.</returns>
        /// <param name="firstDate">First date.</param>
        /// <param name="secondDate">Second date.</param>
        static bool IsFirstDateHigher(DateTime firstDate, DateTime secondDate)
        {
            return firstDate.ToUniversalTime().CompareTo(secondDate.ToUniversalTime()) == 1;
        }
    }
}