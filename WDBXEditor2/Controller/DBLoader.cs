using DBCD;
using DBDefsLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using WDBXEditor2.Views;
using static DBDefsLib.Structs;

namespace WDBXEditor2.Controller
{
    public class DBLoader
    {
        public ConcurrentDictionary<string, IDBCDStorage> LoadedDBFiles;
        public ConcurrentDictionary<string, string> LoadedDBFilePaths;

        private readonly DBDProvider dbdProvider;
        private readonly DBCProvider dbcProvider;

        public DBLoader()
        {
            dbdProvider = new DBDProvider();
            dbcProvider = new DBCProvider();

            LoadedDBFiles = new ConcurrentDictionary<string, IDBCDStorage>();
            LoadedDBFilePaths = new ConcurrentDictionary<string, string>();
        }

        public string[] LoadFiles(string[] files)
        {
            var loadedFiles = new List<string>();
            var dbcd = new DBCD.DBCD(dbcProvider, dbdProvider);
            Stopwatch stopWatch = null;


            foreach (string db2Path in files)
            {
                string db2Name = Path.GetFileName(db2Path);
                string displayName = db2Name;

                try
                {
                    DefinitionSelect definitionSelect = new DefinitionSelect();
                    definitionSelect.SetDB2Name(db2Name);
                    definitionSelect.SetDefinitionFromVersionDefinitions(GetVersionDefinitionsForDB2(db2Path));
                    definitionSelect.ShowDialog();

                    if (definitionSelect.IsCanceled)
                        continue;

                    stopWatch = new Stopwatch();
                    var storage = dbcd.Load(db2Path, definitionSelect.SelectedVersion, definitionSelect.SelectedLocale);

                    if (LoadedDBFiles.ContainsKey(displayName))
                    {
                        displayName = db2Path;
                    }

                    if (LoadedDBFiles.TryAdd(displayName, storage))
                    {
                        LoadedDBFilePaths.TryAdd(displayName, db2Path);
                        loadedFiles.Add(displayName);
                    }

                    stopWatch.Stop();
                    Console.WriteLine($"Loading File: {db2Name} Elapsed Time: {stopWatch.Elapsed}");
                }
                catch (AggregateException)
                {
                    MessageBox.Show(
                        string.Format("Can't find definitions for {0}.\nCheck your filename, network access, or cached DBD files.", db2Name),
                        "WDBXEditor2",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    MessageBox.Show(
                        string.Format("Cant load {0}.\n{1}", db2Name, ex.Message),
                        "WDBXEditor2",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                }
            }

            return loadedFiles.ToArray();
        }

        public VersionDefinitions[] GetVersionDefinitionsForDB2(string db2File)
        {
            var dbdStream = dbdProvider.StreamForTableName(db2File, null);
            var dbdReader = new DBDReader();
            var databaseDefinition = dbdReader.Read(dbdStream);

            return databaseDefinition.versionDefinitions;
        }
    }
}
