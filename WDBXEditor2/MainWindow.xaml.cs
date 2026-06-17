using DBCD;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using WDBXEditor2.Controller;
using WDBXEditor2.Misc;

namespace WDBXEditor2
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private DBLoader dbLoader = new DBLoader();
        private string currentOpenDB2 = string.Empty;
        private IDBCDStorage openedDB2Storage;
        private List<DBCDRow> currentGridRows = new List<DBCDRow>();
        private int currentPageIndex = 0;
        private const int PageSize = 5000;
        private static readonly HashSet<string> ReadOnlyClientFormats = new HashSet<string>(StringComparer.Ordinal)
        {
            "WDC4"
        };

        public MainWindow()
        {
            InitializeComponent();
            SettingStorage.Initialize();

            Exit.Click += (e, o) => Close();

            Title = $"WDBXEditor2  -  {Constants.Version}";
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "DB2 Files (*.db2)|*.db2",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyComputer)
            };

            if (openFileDialog.ShowDialog() == true)
            {
                var files = openFileDialog.FileNames;

                foreach (string loadedDB in dbLoader.LoadFiles(files))
                    OpenDBItems.Items.Add(loadedDB);
            }
        }

        private async void OpenDBItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Clear DataGrid
            DB2DataGrid.Columns.Clear();
            DB2DataGrid.ItemsSource = new List<string>();
            currentGridRows = new List<DBCDRow>();
            currentPageIndex = 0;
            UpdatePagingControls();

            currentOpenDB2 = (string)OpenDBItems.SelectedItem;
            if (currentOpenDB2 == null)
                return;

            if (dbLoader.LoadedDBFiles.TryGetValue(currentOpenDB2, out IDBCDStorage storage))
            {
                openedDB2Storage = storage;
                await LoadCurrentPage();
            }
        }

        private async Task LoadCurrentPage()
        {
            if (openedDB2Storage == null)
                return;

            var stopWatch = Stopwatch.StartNew();

            ProgressBar.IsIndeterminate = true;
            DB2DataGrid.IsEnabled = false;
            PreviousPageButton.IsEnabled = false;
            NextPageButton.IsEnabled = false;
            Title = $"WDBXEditor2  -  {Constants.Version}  -  Loading {currentOpenDB2}...";

            try
            {
                var selectedDB2 = currentOpenDB2;
                var pageIndex = currentPageIndex;
                var result = await Task.Run(() => BuildPageData(openedDB2Storage, pageIndex));

                if (selectedDB2 != currentOpenDB2 || pageIndex != currentPageIndex)
                    return;

                stopWatch.Stop();
                Console.WriteLine($"Populating Grid: {currentOpenDB2} page {currentPageIndex + 1} Elapsed Time: {stopWatch.Elapsed}");

                currentGridRows = result.Rows;
                DB2DataGrid.ItemsSource = result.Table.DefaultView;
                UpdatePagingControls();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                MessageBox.Show(
                    string.Format("Cant display {0}.\n{1}", currentOpenDB2, ex.Message),
                    "WDBXEditor2",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
            }
            finally
            {
                ProgressBar.IsIndeterminate = false;
                DB2DataGrid.IsEnabled = true;
                UpdatePagingControls();
            }
        }

        private static (DataTable Table, List<DBCDRow> Rows) BuildPageData(IDBCDStorage storage, int pageIndex)
        {
            var data = new DataTable();
            var rows = storage.Values
                .Skip(pageIndex * PageSize)
                .Take(PageSize)
                .ToList();

            if (rows.Count > 0)
            {
                PopulateColumns(rows[0], ref data);
                PopulateDataView(rows, ref data);
            }

            return (data, rows);
        }

        private void UpdatePagingControls()
        {
            if (openedDB2Storage == null)
            {
                PageInfoText.Text = "No DB2 loaded";
                PreviousPageButton.IsEnabled = false;
                NextPageButton.IsEnabled = false;
                return;
            }

            int totalRows = openedDB2Storage.Values.Count;
            int totalPages = Math.Max(1, (int)Math.Ceiling(totalRows / (double)PageSize));
            int firstRow = totalRows == 0 ? 0 : currentPageIndex * PageSize + 1;
            int lastRow = Math.Min(totalRows, (currentPageIndex + 1) * PageSize);

            PageInfoText.Text = $"Page {currentPageIndex + 1:N0} / {totalPages:N0}  Rows {firstRow:N0}-{lastRow:N0} of {totalRows:N0}";
            PreviousPageButton.IsEnabled = currentPageIndex > 0;
            NextPageButton.IsEnabled = currentPageIndex < totalPages - 1;
            Title = $"WDBXEditor2  -  {Constants.Version}  -  {currentOpenDB2}  -  {PageInfoText.Text}";
        }

        /// <summary>
        /// Populate the DataView with the DB2 Columns.
        /// </summary>
        private static void PopulateColumns(DBCDRow firstItem, ref DataTable data)
        {
            foreach (string columnName in firstItem.GetDynamicMemberNames())
            {
                var columnValue = firstItem[columnName];

                if (columnValue.GetType().IsArray)
                {
                    Array columnValueArray = (Array)columnValue;
                    for (var i = 0; i < columnValueArray.Length; ++i)
                        data.Columns.Add(columnName + i);
                }
                else
                    data.Columns.Add(columnName);
            }
        }

        /// <summary>
        /// Populate the DataView with the DB2 Data.
        /// </summary>
        private static void PopulateDataView(IEnumerable<DBCDRow> rows, ref DataTable data)
        {
            foreach (var rowData in rows)
            {
                var row = data.NewRow();

                foreach (string columnName in rowData.GetDynamicMemberNames())
                {
                    var columnValue = rowData[columnName];

                    if (columnValue.GetType().IsArray)
                    {
                        Array columnValueArray = (Array)columnValue;
                        for (var i = 0; i < columnValueArray.Length; ++i)
                            row[columnName + i] = columnValueArray.GetValue(i);
                    }
                    else
                        row[columnName] = columnValue;
                }

                data.Rows.Add(row);
            }
        }

        /// <summary>
        /// Close the currently opened DB2 file.
        /// </summary>
        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Title = $"WDBXEditor2  -  {Constants.Version}";

            // Remove the DB2 file from the open files.
            OpenDBItems.Items.Remove(currentOpenDB2);

            // Clear DataGrid
            DB2DataGrid.Columns.Clear();

            currentOpenDB2 = string.Empty;
            openedDB2Storage = null;
            currentGridRows = new List<DBCDRow>();
            currentPageIndex = 0;
            UpdatePagingControls();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(currentOpenDB2))
                return;

            if (!dbLoader.LoadedDBFilePaths.TryGetValue(currentOpenDB2, out string originalPath))
            {
                MessageBox.Show(
                    $"Cant find original path for {currentOpenDB2}. Use Save As to choose a target file.",
                    "WDBXEditor2",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return;
            }

            SaveStorageToFile(originalPath);
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(currentOpenDB2))
                return;

            var saveFileDialog = new SaveFileDialog
            {
                FileName = currentOpenDB2,
                Filter = "DB2 Files (*.db2)|*.db2",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyComputer)
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                SaveStorageToFile(saveFileDialog.FileName);
            }
        }

        private void SaveStorageToFile(string targetFile)
        {
            if (string.IsNullOrEmpty(currentOpenDB2) ||
                !dbLoader.LoadedDBFiles.TryGetValue(currentOpenDB2, out IDBCDStorage storage))
                return;

            if (ReadOnlyClientFormats.Contains(storage.FormatIdentifier))
            {
                MessageBox.Show(
                    $"{Path.GetFileName(targetFile)} is {storage.FormatIdentifier}.\n\n" +
                    "Saving modern client DB2 formats is disabled in this build because the bundled writer rewrites table sections and can produce files the WoW client rejects with ERROR #134.\n\n" +
                    "The file was not changed.",
                    "WDBXEditor2",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return;
            }

            try
            {
                if (storage.FormatIdentifier == "WDC5" &&
                    dbLoader.LoadedDBFilePaths.TryGetValue(currentOpenDB2, out string sourceFile) &&
                    File.Exists(sourceFile))
                {
                    Wdc5InPlacePatcher.Save(storage, sourceFile, targetFile);
                }
                else
                {
                    SafeSaveStorage(storage, targetFile);
                }

                MessageBox.Show(
                    $"Saved {Path.GetFileName(targetFile)}.\nA backup was created next to the target file.",
                    "WDBXEditor2",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Cant save {Path.GetFileName(targetFile)}.\n{ex.Message}",
                    "WDBXEditor2",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        private static void SafeSaveStorage(IDBCDStorage storage, string targetFile)
        {
            string targetDirectory = Path.GetDirectoryName(targetFile);
            if (string.IsNullOrEmpty(targetDirectory))
                targetDirectory = Directory.GetCurrentDirectory();

            Directory.CreateDirectory(targetDirectory);

            string tempFile = Path.Combine(
                targetDirectory,
                $".{Path.GetFileName(targetFile)}.{Guid.NewGuid():N}.tmp"
            );

            try
            {
                storage.Save(tempFile);
                ValidateSavedFile(storage, tempFile);

                if (File.Exists(targetFile))
                {
                    string backupFile = $"{targetFile}.{DateTime.Now:yyyyMMddHHmmss}.bak";
                    File.Copy(targetFile, backupFile, overwrite: false);
                }

                File.Copy(tempFile, targetFile, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }

        private static void ValidateSavedFile(IDBCDStorage storage, string savedFile)
        {
            using var stream = File.Open(savedFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            var parser = new DBCD.IO.DBParser(stream);

            if (parser.Identifier != storage.FormatIdentifier)
                throw new InvalidDataException($"Saved DB2 format changed from {storage.FormatIdentifier} to {parser.Identifier}.");

            if (parser.LayoutHash != storage.LayoutHash)
                throw new InvalidDataException($"Saved DB2 layout hash changed from 0x{storage.LayoutHash:X8} to 0x{parser.LayoutHash:X8}.");

            if (parser.RecordsCount != storage.Values.Count)
                throw new InvalidDataException($"Saved DB2 row count changed from {storage.Values.Count} to {parser.RecordsCount}.");
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private async void PreviousPage_Click(object sender, RoutedEventArgs e)
        {
            if (openedDB2Storage == null || currentPageIndex <= 0)
                return;

            currentPageIndex--;
            await LoadCurrentPage();
        }

        private async void NextPage_Click(object sender, RoutedEventArgs e)
        {
            if (openedDB2Storage == null)
                return;

            int totalPages = Math.Max(1, (int)Math.Ceiling(openedDB2Storage.Values.Count / (double)PageSize));
            if (currentPageIndex >= totalPages - 1)
                return;

            currentPageIndex++;
            await LoadCurrentPage();
        }

        private void DB2DataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction == DataGridEditAction.Commit)
            {
                if (e.Column != null)
                {
                    var rowIdx = e.Row.GetIndex();
                    if (rowIdx < 0 || rowIdx >= currentGridRows.Count)
                        throw new Exception();

                    var newVal = e.EditingElement as TextBox;

                    if (newVal == null)
                        return;

                    var dbcRow = currentGridRows[rowIdx];
                    SetColumnValue(dbcRow, e.Column.Header.ToString(), newVal.Text);

                    Console.WriteLine($"RowIdx: {rowIdx} Text: {newVal.Text}");
                }
            }
        }

        private static void SetColumnValue(DBCDRow row, string columnName, string value)
        {
            var arrayColumnMatch = Regex.Match(columnName, @"^(?<name>.+?)(?<index>\d+)$");
            if (arrayColumnMatch.Success)
            {
                row[
                    arrayColumnMatch.Groups["name"].Value,
                    int.Parse(arrayColumnMatch.Groups["index"].Value)
                ] = value;
                return;
            }

            row[columnName] = value;
        }
    }
}
