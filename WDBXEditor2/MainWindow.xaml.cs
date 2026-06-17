using DBCD;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
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
        private const int MaxPreviewRows = 5000;

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

            currentOpenDB2 = (string)OpenDBItems.SelectedItem;
            if (currentOpenDB2 == null)
                return;

            if (dbLoader.LoadedDBFiles.TryGetValue(currentOpenDB2, out IDBCDStorage storage))
            {
                var stopWatch = new Stopwatch();
                stopWatch.Start();

                ProgressBar.IsIndeterminate = true;
                DB2DataGrid.IsEnabled = false;
                Title = $"WDBXEditor2  -  {Constants.Version}  -  Loading {currentOpenDB2}...";

                try
                {
                    var selectedDB2 = currentOpenDB2;
                    var result = await Task.Run(() => BuildPreviewData(storage));

                    if (selectedDB2 != currentOpenDB2)
                        return;

                    stopWatch.Stop();
                    Console.WriteLine($"Populating Grid: {currentOpenDB2} Elapsed Time: {stopWatch.Elapsed}");

                    openedDB2Storage = storage;
                    currentGridRows = result.Rows;
                    DB2DataGrid.ItemsSource = result.Table.DefaultView;

                    var previewSuffix = storage.Values.Count > result.Rows.Count
                        ? $"  -  showing {result.Rows.Count:N0} of {storage.Values.Count:N0} rows"
                        : $"  -  {result.Rows.Count:N0} rows";
                    Title = $"WDBXEditor2  -  {Constants.Version}  -  {currentOpenDB2}{previewSuffix}";
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
                }
            }
        }

        private static (DataTable Table, List<DBCDRow> Rows) BuildPreviewData(IDBCDStorage storage)
        {
            var data = new DataTable();
            var rows = storage.Values.Take(MaxPreviewRows).ToList();

            if (rows.Count > 0)
            {
                PopulateColumns(rows[0], ref data);
                PopulateDataView(rows, ref data);
            }

            return (data, rows);
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
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(currentOpenDB2))
                dbLoader.LoadedDBFiles[currentOpenDB2].Save(currentOpenDB2);
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
                dbLoader.LoadedDBFiles[currentOpenDB2].Save(saveFileDialog.FileName);
            }
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
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
