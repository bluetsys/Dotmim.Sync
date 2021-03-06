﻿using Dotmim.Sync.Builders;
using System;
using System.Text;

using System.Data.Common;

using System.Data;
using MySql.Data.MySqlClient;
using System.Linq;
using Dotmim.Sync.MySql.Builders;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Dotmim.Sync.MySql
{
    public class MySqlBuilderTrackingTable : IDbBuilderTrackingTableHelper
    {
        private ParserName tableName;
        private ParserName trackingName;
        private SyncTable tableDescription;
        private MySqlConnection connection;
        private MySqlTransaction transaction;
        private MySqlDbMetadata mySqlDbMetadata;


        public MySqlBuilderTrackingTable(SyncTable tableDescription, ParserName tableName, ParserName trackingName, SyncSetup setup, DbConnection connection, DbTransaction transaction = null)
        {
            this.connection = connection as MySqlConnection;
            this.transaction = transaction as MySqlTransaction;
            this.tableDescription = tableDescription;
            this.tableName = tableName;
            this.trackingName = trackingName;
            this.mySqlDbMetadata = new MySqlDbMetadata();
        }

        public async Task<bool> NeedToCreateTrackingTableAsync()
             => !(await MySqlManagementUtils.TableExistsAsync(connection, transaction, trackingName).ConfigureAwait(false));

        public Task CreateIndexAsync() => Task.CompletedTask;
        public Task CreatePkAsync() => Task.CompletedTask;

        public async Task CreateTableAsync()
        {
            bool alreadyOpened = this.connection.State == ConnectionState.Open;

            try
            {
                using (var command = new MySqlCommand())
                {
                    if (!alreadyOpened)
                        await this.connection.OpenAsync().ConfigureAwait(false);

                    if (this.transaction != null)
                        command.Transaction = this.transaction;

                    command.CommandText = this.CreateTableCommandText();
                    command.Connection = this.connection;
                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during CreateIndex : {ex}");
                throw;

            }
            finally
            {
                if (!alreadyOpened && this.connection.State != ConnectionState.Closed)
                    this.connection.Close();

            }


        }

        public string CreateTableCommandText()
        {
            var stringBuilder = new StringBuilder();
            stringBuilder.AppendLine($"CREATE TABLE {trackingName.Quoted().ToString()} (");

            // Adding the primary key
            foreach (var pkColumn in this.tableDescription.GetPrimaryKeysColumns())
            {
                var columnName = ParserName.Parse(pkColumn, "`").Quoted().ToString();
                var columnTypeString = this.mySqlDbMetadata.TryGetOwnerDbTypeString(pkColumn.OriginalDbType, pkColumn.GetDbType(), false, false, pkColumn.MaxLength, this.tableDescription.OriginalProvider, MySqlSyncProvider.ProviderType);
                var unQuotedColumnType = ParserName.Parse(columnTypeString, "`").Unquoted().Normalized().ToString();

                var columnPrecisionString = this.mySqlDbMetadata.TryGetOwnerDbTypePrecision(pkColumn.OriginalDbType, pkColumn.GetDbType(), false, false, pkColumn.MaxLength, pkColumn.Precision, pkColumn.Scale, this.tableDescription.OriginalProvider, MySqlSyncProvider.ProviderType);
                var columnType = $"{unQuotedColumnType} {columnPrecisionString}";

                stringBuilder.AppendLine($"{columnName} {columnType} NOT NULL, ");
            }

            // adding the tracking columns
            stringBuilder.AppendLine($"`update_scope_id` VARCHAR(36) NULL, ");
            stringBuilder.AppendLine($"`timestamp` BIGINT NULL, ");
            stringBuilder.AppendLine($"`sync_row_is_tombstone` BIT NOT NULL default 0, ");
            stringBuilder.AppendLine($"`last_change_datetime` DATETIME NULL, ");

            stringBuilder.Append(" PRIMARY KEY (");

            var comma = "";
            foreach (var pkColumn in this.tableDescription.GetPrimaryKeysColumns())
            {
                var quotedColumnName = ParserName.Parse(pkColumn, "`").Quoted().ToString();

                stringBuilder.Append(comma);
                stringBuilder.Append(quotedColumnName);

                comma = ", ";
            }
            stringBuilder.Append("))");

            return stringBuilder.ToString();
        }


        public async Task DropTableAsync()
        {
            var commandText = $"drop table if exists {trackingName.Quoted().ToString()}";

            bool alreadyOpened = connection.State == ConnectionState.Open;

            try
            {
                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                using (var command = new MySqlCommand(commandText, connection))
                {
                    if (transaction != null)
                        command.Transaction = transaction;

                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during DropTableCommand : {ex}");
                throw;
            }
            finally
            {
                if (!alreadyOpened && connection.State != ConnectionState.Closed)
                    connection.Close();

            }

        }

        public async Task RenameTableAsync(ParserName oldTableName)
        {

            var tableNameString = this.trackingName.Quoted().ToString();
            var oldTableNameString = oldTableName.Quoted().ToString();

            var commandText = $"RENAME TABLE {oldTableNameString} TO {tableNameString}; ";

            bool alreadyOpened = connection.State == ConnectionState.Open;

            try
            {
                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                using (var command = new MySqlCommand(commandText, connection))
                {
                    if (transaction != null)
                        command.Transaction = transaction;

                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during RenameTableAsync : {ex}");
                throw;
            }
            finally
            {
                if (!alreadyOpened && connection.State != ConnectionState.Closed)
                    connection.Close();

            }

        }
    }
}
