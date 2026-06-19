using DBCD.Providers;
using DBDefsLib;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using WDBXEditor2.Controller;

namespace DBCD.Tests
{
    [TestClass]
    public class ReadingTest
    {
        static GithubDBDProvider githubDBDProvider = new(true);
        static readonly WagoDBCProvider wagoDBCProvider = new();

        // Disabled as 7.1.0 definitions are not yet generally available
        /*
        [TestMethod]
        public void TestWDB5ReadingNoIndexData()
        {
            DBCD dbcd = new(wagoDBCProvider, githubDBDProvider);
            IDBCDStorage storage = dbcd.Load("Achievement_Category", "7.1.0.23222");
            var row = storage[1];
            Assert.AreEqual("Statistics", row["Name_lang"]);
        }
        */
        [TestMethod]
        public void TestWDB5Reading()
        {
            DBCD dbcd = new(wagoDBCProvider, githubDBDProvider);
            IDBCDStorage storage = dbcd.Load("Map", "7.1.0.23222");
            var row = storage[451];
            Assert.AreEqual("development", row["Directory"]);
        }

        [TestMethod]
        public void TestWDC1Reading()
        {
            DBCD dbcd = new(wagoDBCProvider, githubDBDProvider);
            IDBCDStorage storage = dbcd.Load("Map", "7.3.5.25600");

            var row = storage[451];
            Assert.AreEqual("development", row["Directory"]);
        }

        [TestMethod]
        public void TestWDC2Reading()
        {
            DBCD dbcd = new(wagoDBCProvider, githubDBDProvider);
            IDBCDStorage storage = dbcd.Load("Map", "8.0.1.26231");

            var row = storage[451];
            Assert.AreEqual("development", row["Directory"]);
        }

        [TestMethod]
        public void TestWDC3Reading()
        {
            DBCD dbcd = new(wagoDBCProvider, githubDBDProvider);
            IDBCDStorage storage = dbcd.Load("Map", "9.2.7.45745");

            var row = storage[451];
            Assert.AreEqual("development", row["Directory"]);
        }

        [TestMethod]
        public void TestWDC4Reading()
        {
            DBCD dbcd = new(wagoDBCProvider, githubDBDProvider);
            IDBCDStorage storage = dbcd.Load("Map", "10.1.0.48480");

            var row = storage[2574];
            Assert.AreEqual("Dragon Isles", row["MapName_lang"]);
        }

        [TestMethod]
        public void TestWDC5Reading()
        {
            DBCD dbcd = new(wagoDBCProvider, githubDBDProvider);
            IDBCDStorage storage = dbcd.Load("Map", "10.2.5.52432");

            var row = storage[2574];
            Assert.AreEqual("Dragon Isles", row["MapName_lang"]);
        }

        [TestMethod]
        public void TestSpellInterruptsRuntimeArraySemantics()
        {
            DBCD dbcd = new(wagoDBCProvider, githubDBDProvider);
            IDBCDStorage storage = dbcd.Load("SpellInterrupts", "9.0.1.35755");

            var recordType = storage.Values.First().GetUnderlyingType();
            var auraInterruptFlags = recordType.GetField("AuraInterruptFlags", BindingFlags.Instance | BindingFlags.Public);
            var channelInterruptFlags = recordType.GetField("ChannelInterruptFlags", BindingFlags.Instance | BindingFlags.Public);

            Assert.IsNotNull(auraInterruptFlags);
            Assert.IsNotNull(channelInterruptFlags);
            Assert.IsTrue(auraInterruptFlags.FieldType.IsArray);
            Assert.IsTrue(channelInterruptFlags.FieldType.IsArray);
            Assert.AreEqual(2, GetCardinalityCount(auraInterruptFlags));
            Assert.AreEqual(2, GetCardinalityCount(channelInterruptFlags));
        }

        [TestMethod]
        public void TestChrCustomizationChoiceRuntimeFieldSemantics()
        {
            DBCD dbcd = new(wagoDBCProvider, githubDBDProvider);
            IDBCDStorage storage = dbcd.Load("ChrCustomizationChoice", "9.2.7.45745");

            var recordType = storage.Values.First().GetUnderlyingType();
            var nameLang = recordType.GetField("Name_lang", BindingFlags.Instance | BindingFlags.Public);
            var swatchColor = recordType.GetField("SwatchColor", BindingFlags.Instance | BindingFlags.Public);

            Assert.IsNotNull(nameLang);
            Assert.IsNotNull(swatchColor);
            Assert.AreEqual(typeof(string), nameLang.FieldType);
            Assert.IsTrue(swatchColor.FieldType.IsArray);
            Assert.AreEqual(2, GetCardinalityCount(swatchColor));
        }

        [TestMethod]
        public void TestLocalDefinitionSpellInterruptsSemantics()
        {
            var definition = ReadLocalDefinition("SpellInterrupts");
            var version = ResolveVersionDefinition(definition, "9.0.1.35755");

            var auraInterruptFlags = version.definitions.First(field => field.name == "AuraInterruptFlags");
            var channelInterruptFlags = version.definitions.First(field => field.name == "ChannelInterruptFlags");

            Assert.AreEqual(2, auraInterruptFlags.arrLength);
            Assert.AreEqual(2, channelInterruptFlags.arrLength);
        }

        [TestMethod]
        public void TestLocalDefinitionChrCustomizationChoiceSemantics()
        {
            var definition = ReadLocalDefinition("ChrCustomizationChoice");
            var version = ResolveVersionDefinition(definition, "9.2.7.45745");

            var swatchColor = version.definitions.First(field => field.name == "SwatchColor");

            Assert.AreEqual("locstring", definition.columnDefinitions["Name_lang"].type);
            Assert.AreEqual(2, swatchColor.arrLength);
        }

        [TestMethod]
        public void TestLocalSpellCooldownsSampleCanLoad()
        {
            string samplePath = "/Users/wuming/Downloads/Spell/SpellCooldowns.db2";
            Assert.IsTrue(File.Exists(samplePath), $"缺少本地样本: {samplePath}");

            var dbcd = new DBCD(
                new FilesystemDBCProvider(Path.GetDirectoryName(samplePath)),
                new FilesystemDBDProvider(GetLocalDefinitionsDirectory())
            );

            IDBCDStorage storage = dbcd.Load("SpellCooldowns", null);
            var recordType = storage.Values.First().GetUnderlyingType();
            var recoveryTime = recordType.GetField("RecoveryTime", BindingFlags.Instance | BindingFlags.Public);
            var startRecoveryTime = recordType.GetField("StartRecoveryTime", BindingFlags.Instance | BindingFlags.Public);

            Assert.AreEqual("WDC5", storage.FormatIdentifier);
            Assert.IsTrue(storage.Count > 0);
            Assert.IsNotNull(recoveryTime);
            Assert.IsNotNull(startRecoveryTime);
            Assert.IsFalse(recoveryTime.FieldType.IsArray);
            Assert.IsFalse(startRecoveryTime.FieldType.IsArray);
        }

        [TestMethod]
        public void TestLocalSpellInterruptsSampleCanLoadWithArrayField()
        {
            string samplePath = "/Users/wuming/Downloads/Spell/SpellInterrupts.db2";
            Assert.IsTrue(File.Exists(samplePath), $"缺少本地样本: {samplePath}");

            var dbcd = new DBCD(
                new FilesystemDBCProvider(Path.GetDirectoryName(samplePath)),
                new FilesystemDBDProvider(GetLocalDefinitionsDirectory())
            );

            IDBCDStorage storage = dbcd.Load("SpellInterrupts", null);
            var recordType = storage.Values.First().GetUnderlyingType();
            var auraInterruptFlags = recordType.GetField("AuraInterruptFlags", BindingFlags.Instance | BindingFlags.Public);

            Assert.AreEqual("WDC5", storage.FormatIdentifier);
            Assert.IsNotNull(auraInterruptFlags);
            Assert.IsTrue(auraInterruptFlags.FieldType.IsArray);
            Assert.AreEqual(2, GetCardinalityCount(auraInterruptFlags));
        }

        [TestMethod]
        public void TestLocalSpellSampleCanLoadCurrentBuild()
        {
            string samplePath = "/Users/wuming/Downloads/Spell/Spell.db2";
            Assert.IsTrue(File.Exists(samplePath), $"缺少本地样本: {samplePath}");

            var dbcd = new DBCD(
                new FilesystemDBCProvider(Path.GetDirectoryName(samplePath)),
                new FilesystemDBDProvider(GetLocalDefinitionsDirectory())
            );

            IDBCDStorage storage = dbcd.Load("Spell", "12.0.5.66741");

            Assert.AreEqual("WDC5", storage.FormatIdentifier);
            Assert.IsTrue(storage.Count > 0);
        }

        [TestMethod]
        public void TestLocalSpellCooldownsWdc5PatchSave()
        {
            string sourcePath = "/Users/wuming/Downloads/Spell/SpellCooldowns.db2";
            Assert.IsTrue(File.Exists(sourcePath), $"缺少本地样本: {sourcePath}");

            string workDir = CreateLocalSampleWorkDir();
            string targetPath = Path.Combine(workDir, "SpellCooldowns.db2");
            File.Copy(sourcePath, targetPath, overwrite: true);

            var dbcd = new DBCD(
                new FilesystemDBCProvider(workDir),
                new FilesystemDBDProvider(GetLocalDefinitionsDirectory())
            );

            IDBCDStorage storage = dbcd.Load("SpellCooldowns", null);
            var row = storage.Values.First();

            uint originalRecoveryTime = Convert.ToUInt32(row["RecoveryTime"]);
            uint patchedRecoveryTime = storage.Values
                .Select(item => Convert.ToUInt32(item["RecoveryTime"]))
                .First(value => value != originalRecoveryTime);
            row["RecoveryTime"] = patchedRecoveryTime;

            var result = Wdc5InPlacePatcher.Save(storage, sourcePath, targetPath);
            Assert.IsTrue(result.EditableRows > 0);

            var reloaded = dbcd.Load("SpellCooldowns", null);
            Assert.AreEqual(patchedRecoveryTime, Convert.ToUInt32(reloaded[row.ID]["RecoveryTime"]));
        }

        [TestMethod]
        public void TestLocalSpellInterruptsWdc5PatchSave()
        {
            string sourcePath = "/Users/wuming/Downloads/Spell/SpellInterrupts.db2";
            Assert.IsTrue(File.Exists(sourcePath), $"缺少本地样本: {sourcePath}");

            string workDir = CreateLocalSampleWorkDir();
            string targetPath = Path.Combine(workDir, "SpellInterrupts.db2");
            File.Copy(sourcePath, targetPath, overwrite: true);

            var dbcd = new DBCD(
                new FilesystemDBCProvider(workDir),
                new FilesystemDBDProvider(GetLocalDefinitionsDirectory())
            );

            IDBCDStorage storage = dbcd.Load("SpellInterrupts", null);
            var row = storage.Values.First(item => item["AuraInterruptFlags"] is Array array && array.Length == 2);
            var original = (Array)row["AuraInterruptFlags"];
            uint original0 = Convert.ToUInt32(original.GetValue(0));
            uint original1 = Convert.ToUInt32(original.GetValue(1));
            uint patched0 = original0 == 0 ? 1u : 0u;

            row["AuraInterruptFlags", 0] = patched0;

            var result = Wdc5InPlacePatcher.Save(storage, sourcePath, targetPath);
            Assert.IsTrue(result.EditableRows > 0);

            var reloaded = dbcd.Load("SpellInterrupts", null);
            var reloadedArray = (Array)reloaded[row.ID]["AuraInterruptFlags"];
            Assert.AreEqual(patched0, Convert.ToUInt32(reloadedArray.GetValue(0)));
            Assert.AreEqual(original1, Convert.ToUInt32(reloadedArray.GetValue(1)));
        }

        private static int? GetCardinalityCount(FieldInfo field)
        {
            var attribute = field
                .GetCustomAttributes(inherit: false)
                .FirstOrDefault(attr => string.Equals(attr.GetType().Name, "CardinalityAttribute", StringComparison.Ordinal));

            if (attribute == null)
                return null;

            var countField = attribute.GetType().GetField("Count", BindingFlags.Instance | BindingFlags.Public);
            if (countField == null)
                return null;

            return countField.GetValue(attribute) as int? ?? (int?)Convert.ToInt32(countField.GetValue(attribute));
        }

        private static Structs.DBDefinition ReadLocalDefinition(string tableName)
        {
            string path = Path.GetFullPath(Path.Combine(
                GetLocalDefinitionsDirectory(),
                tableName + ".dbd"
            ));

            using var stream = File.OpenRead(path);
            return new DBDReader().Read(stream);
        }

        private static Structs.VersionDefinitions ResolveVersionDefinition(Structs.DBDefinition definition, string build)
        {
            Structs.VersionDefinitions? versionDefinition = null;
            Utils.GetVersionDefinitionByBuild(definition, new Build(build), out versionDefinition);

            if (!versionDefinition.HasValue)
                throw new InvalidOperationException($"本地 definition 中找不到 build {build}。");

            return versionDefinition.Value;
        }

        private static string GetLocalDefinitionsDirectory()
        {
            return Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "WDBXEditor2",
                "definitions"
            ));
        }

        private static string CreateLocalSampleWorkDir()
        {
            string path = Path.Combine(Path.GetTempPath(), "wdbxeditor2-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        [TestMethod]
        public void TestWDC5ReadingBDBDNoCache()
        {
            DBCD dbcd = new(wagoDBCProvider, GithubBDBDProvider.GetStream(true));
            IDBCDStorage storage = dbcd.Load("Map", "10.2.5.52432");

            var row = storage[2574];
            Assert.AreEqual("Dragon Isles", row["MapName_lang"]);
        }

        [TestMethod]
        public void TestSparseReading()
        {
            DBCD dbcd = new(wagoDBCProvider, githubDBDProvider);
            IDBCDStorage storage = dbcd.Load("ItemSparse", "9.2.7.45745");

            var row = storage[132172];
            Assert.AreEqual("Crowbar", row["Display_lang"]);
        }

        [TestMethod]
        public void TestNonInlineRelation()
        {
            DBCD dbcd = new(wagoDBCProvider, githubDBDProvider);
            IDBCDStorage storage = dbcd.Load("MapDifficulty", "9.2.7.45745");

            var row = storage[38];
            Assert.AreEqual(451, row["MapID"]);
        }

        [TestMethod]
        public void TestEncryptedInfo()
        {
            DBCD dbcd = new DBCD(wagoDBCProvider, githubDBDProvider);

            var storage = dbcd.Load("SpellName", "11.0.2.55959");

            foreach (var section in storage.GetEncryptedSections())
            {
                Console.WriteLine($"Found encrypted section encrypted with key {section.Key} containing {section.Value} rows");
            }
        }

        [TestMethod]
        public void TestGithubDBDProviderNoCache()
        {
            var noCacheProvider = new GithubDBDProvider(false);
            noCacheProvider.StreamForTableName("ItemSparse");
        }

        [TestMethod]
        public void TestGithubDBDProviderWithCache()
        {
            githubDBDProvider.StreamForTableName("ItemSparse");
        }

        [TestMethod]
        public void TestReadingAllDB2s()
        {
            return; // Only run this test manually
            var localDBDProvider = new FilesystemDBDProvider("D:\\Projects\\WoWDBDefs\\definitions");

            //var build = "3.3.5.12340"; // WDBC
            //var build = "6.0.1.18179"; // WDB2
            //var build = "7.0.1.20740"; // WDB3, only 1 DBD sadly
            //var build = "7.0.1.20810"; // WDB4, only 2 DBDs sadly
            //var build = "7.2.0.23436"; // WDB5, only Map.db2
            //var build = "7.3.5.25928"; // WDB6
            //var build = "7.3.5.25928"; // WDC1
            //var build = "8.0.1.26231"; // WDC2
            //var build = "9.1.0.39653"; // WDC3
            //var build = "10.1.0.48480"; // WDC4
            var build = "11.0.2.56044"; // WDC5

            var localDBCProvider = new FilesystemDBCProvider(Path.Combine("DBCCache", build));
            var dbcd = new DBCD(localDBCProvider, localDBDProvider);
            var allDB2s = wagoDBCProvider.GetAllTableNames();

            var attemptedTables = 0;
            var successfulTables = 0;

            foreach (var tableName in allDB2s)
            {
                // I think this table is meant to crash the test, so we skip it
                if (tableName == "UnitTestSparse")
                    continue;

                if (!localDBDProvider.ContainsBuild(tableName, build))
                    continue;

                attemptedTables++;

                try
                {
                    var storage = dbcd.Load(tableName, build);
                    successfulTables++;
                }
                catch (FileNotFoundException e)
                {
                    Console.WriteLine($"Failed to load {tableName} for build {build}, does not exist in build.");
                    successfulTables++; // this counts
                }
                catch (Exception e)
                {
                    Console.WriteLine("Failed to load " + tableName + " for build " + build + ": " + e.Message + "\n" + e.StackTrace);
                }
            }

            Assert.AreEqual(attemptedTables, successfulTables);
        }

        //[TestMethod]
        //public void TestHotfixApplying()
        //{
        //    DBCD dbcd = new DBCD(dbcProvider, githubDBDProvider);

        //    var storage = dbcd.Load("ItemSparse");
        //    var hotfix = new HotfixReader("hotfix.bin");

        //    var countBefore = storage.Count;
        //    storage = storage.ApplyingHotfixes(hotfix);

        //    var countAfter = storage.Count;

        //    System.Console.WriteLine($"B: {countBefore} => A: {countAfter}");
        //}


        //[TestMethod]
        //public void TestFilesystemDBDProvider()
        //{
        //    DBCD dbcd = new DBCD(dbcProvider, dbdProvider);
        //    var storage = dbcd.Load("SpellName", locale: Locale.EnUS);
        //    // Spell is present in Classic Era -> Retail: https://www.wowhead.com/spell=17/
        //    Assert.AreEqual("Power Word: Shield", storage[17]["Name_lang"]);
        //}
    }
}
