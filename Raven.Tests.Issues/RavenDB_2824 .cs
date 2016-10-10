// -----------------------------------------------------------------------
//  <copyright file="RavenDB_2824 .cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System.Linq;
using Raven.Abstractions.Data;
using Raven.Abstractions.Extensions;
using Raven.Database.Config;
using Raven.Json.Linq;
using Raven.Tests.Common;
using Xunit;
using Xunit.Extensions;

namespace Raven.Tests.Issues
{
    public class RavenDB_2824 : RavenTest
    {
        private readonly string BackupDir;

        public RavenDB_2824()
        {
            BackupDir = NewDataPath("BackupDatabase");
        }

        protected override void ModifyConfiguration(InMemoryRavenConfiguration config)
        {
            config.Settings[Constants.Esent.CircularLog] = "false";
            config.Settings[Constants.Voron.AllowIncrementalBackups] = "true";
            config.Storage.Voron.AllowIncrementalBackups = true;
        }

        [Theory]
        [PropertyData("Storages")]
        public void ShouldThrowWhenTryingToUseTheSameIncrementalBackupLocationForDifferentDatabases(string storageName)
        {
            using (var store = NewRemoteDocumentStore(runInMemory: false, requestedStorage: storageName, databaseName: "RavenDB_2824_one"))
            {
                store.DatabaseCommands.Put("animals/1", null, RavenJObject.Parse("{'Name':'Daisy'}"), new RavenJObject());

                store.DatabaseCommands.GlobalAdmin.StartBackup(BackupDir, null, true, store.DefaultDatabase);
                WaitForBackup(store.DatabaseCommands, true);

                store.DatabaseCommands.Put("animals/2", null, RavenJObject.Parse("{'Name':'Banny'}"), new RavenJObject());

                store.DatabaseCommands.GlobalAdmin.StartBackup(BackupDir, null, true, store.DefaultDatabase);
                WaitForBackup(store.DatabaseCommands, true);
            }

            using (var store = NewRemoteDocumentStore(runInMemory: false, requestedStorage: storageName, databaseName: "RavenDB_2824_two"))
            {
                store.DatabaseCommands.Put("animals/1", null, RavenJObject.Parse("{'Name':'Daisy'}"), new RavenJObject());

                store.DatabaseCommands.GlobalAdmin.StartBackup(BackupDir, null, true, "RavenDB_2824_two"); // use the same BackupDir on purpose
                WaitForBackup(store.DatabaseCommands, false);

                var backupStatus = store.DatabaseCommands.Get(BackupStatus.RavenBackupStatusDocumentKey).DataAsJson.JsonDeserialization<BackupStatus>();

                var errorMessage = backupStatus.Messages.FirstOrDefault(x => x.Severity == BackupStatus.BackupMessageSeverity.Error);

                Assert.NotNull(errorMessage);

                Assert.Contains("Can't perform an incremental backup to a given folder because it already contains incremental backup data of different database. Existing incremental data origins from 'RavenDB_2824_one' database.", errorMessage.Message);
            }
        }

    }
}
