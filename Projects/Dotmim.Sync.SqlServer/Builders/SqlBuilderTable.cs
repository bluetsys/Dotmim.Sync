﻿using Dotmim.Sync.Builders;


using Dotmim.Sync.SqlServer.Manager;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Dotmim.Sync.SqlServer.Builders
{
    public class SqlBuilderTable : IDbBuilderTableHelper
    {
        private ParserName tableName;
        private ParserName trackingName;
        private SyncTable tableDescription;
        private readonly SyncSetup setup;
        private SqlConnection connection;
        private SqlTransaction transaction;
        private SqlDbMetadata sqlDbMetadata;


        public SqlBuilderTable(SyncTable tableDescription, ParserName tableName, ParserName trackingName, SyncSetup setup, DbConnection connection, DbTransaction transaction = null)
        {
            this.connection = connection as SqlConnection;
            this.transaction = transaction as SqlTransaction;

            this.tableDescription = tableDescription;
            this.setup = setup;
            this.tableName = tableName;
            this.trackingName = trackingName;
            this.sqlDbMetadata = new SqlDbMetadata();
        }


        private static Dictionary<string, string> createdRelationNames = new Dictionary<string, string>();

        private static string GetRandomString() => 
            Path.GetRandomFileName().Replace(".", "").ToLowerInvariant();

        /// <summary>
        /// Ensure the relation name is correct to be created in MySql
        /// </summary>
        public static string NormalizeRelationName(string relation)
        {
            if (createdRelationNames.ContainsKey(relation))
                return createdRelationNames[relation];

            var name = relation;

            if (relation.Length > 128)
                name = $"{relation.Substring(0, 110)}_{GetRandomString()}";

            // MySql could have a special character in its relation names
            name = name.Replace("~", "").Replace("#", "");

            createdRelationNames.Add(relation, name);

            return name;
        }

        public async Task<bool> NeedToCreateForeignKeyConstraintsAsync(SyncRelation relation)
        {
            // Don't want foreign key on same table since it could be a problem on first 
            // sync. We are not sure that parent row will be inserted in first position
            //if (relation.GetParentTable() == relation.GetTable())
            //    return false;

            string tableName = relation.GetTable().TableName;
            string schemaName = relation.GetTable().SchemaName;
            string fullName = string.IsNullOrEmpty(schemaName) ? tableName : $"{schemaName}.{tableName}";
            var relationName = NormalizeRelationName(relation.RelationName);

            bool alreadyOpened = connection.State == ConnectionState.Open;

            try
            {
                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                var syncTable = await SqlManagementUtils.GetRelationsForTableAsync(connection, transaction, tableName, schemaName).ConfigureAwait(false);

                var foreignKeyExist = syncTable.Rows.Any(r =>
                   string.Equals(r["ForeignKey"].ToString(), relationName, SyncGlobalization.DataSourceStringComparison));

                return !foreignKeyExist;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during checking foreign keys: {ex}");
                throw;
            }
            finally
            {
                if (!alreadyOpened && connection.State != ConnectionState.Closed)
                    connection.Close();
            }
        }
        private SqlCommand BuildForeignKeyConstraintsCommand(SyncRelation constraint)
        {
            var sqlCommand = new SqlCommand();
            var tableName = ParserName.Parse(constraint.GetTable()).Quoted().Schema().ToString();
            var parentTableName = ParserName.Parse(constraint.GetParentTable()).Quoted().Schema().ToString();

            var relationName = NormalizeRelationName(constraint.RelationName);

            var stringBuilder = new StringBuilder();
            stringBuilder.Append("ALTER TABLE ");
            stringBuilder.Append(tableName);
            stringBuilder.AppendLine(" WITH NOCHECK");
            stringBuilder.Append("ADD CONSTRAINT ");
            stringBuilder.AppendLine($"[{relationName}]");
            stringBuilder.Append("FOREIGN KEY (");
            string empty = string.Empty;
            foreach (var column in constraint.Keys)
            {
                var childColumnName = ParserName.Parse(column.ColumnName).Quoted().ToString();
                stringBuilder.Append($"{empty} {childColumnName}");
                empty = ", ";
            }
            stringBuilder.AppendLine(" )");
            stringBuilder.Append("REFERENCES ");
            stringBuilder.Append(parentTableName).Append(" (");
            empty = string.Empty;
            foreach (var parentdColumn in constraint.ParentKeys)
            {
                var parentColumnName = ParserName.Parse(parentdColumn.ColumnName).Quoted().ToString();
                stringBuilder.Append($"{empty} {parentColumnName}");
                empty = ", ";
            }
            stringBuilder.Append(" ) ");
            sqlCommand.CommandText = stringBuilder.ToString();
            return sqlCommand;
        }
        public async Task CreateForeignKeyConstraintsAsync(SyncRelation constraint)
        {

            bool alreadyOpened = connection.State == ConnectionState.Open;

            try
            {
                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                using (var command = BuildForeignKeyConstraintsCommand(constraint))
                {
                    command.Connection = connection;

                    if (transaction != null)
                        command.Transaction = transaction;

                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during CreateForeignKeyConstraints : {ex}");
                throw;

            }
            finally
            {
                if (!alreadyOpened && connection.State != ConnectionState.Closed)
                    connection.Close();

            }

        }
  
        private SqlCommand BuildPkCommand()
        {
            var stringBuilder = new StringBuilder();
            var tableNameString = tableName.Schema().Quoted().ToString();
            var primaryKeyNameString = tableName.Schema().Unquoted().Normalized().ToString();

            stringBuilder.AppendLine($"ALTER TABLE {tableNameString} ADD CONSTRAINT [PK_{primaryKeyNameString}] PRIMARY KEY(");
            for (int i = 0; i < this.tableDescription.PrimaryKeys.Count; i++)
            {
                var pkColumn = this.tableDescription.PrimaryKeys[i];
                var quotedColumnName = ParserName.Parse(pkColumn).Quoted().ToString();
                stringBuilder.Append(quotedColumnName);

                if (i < this.tableDescription.PrimaryKeys.Count - 1)
                    stringBuilder.Append(", ");
            }
            stringBuilder.Append(")");

            return new SqlCommand(stringBuilder.ToString());
        }
        public async Task CreatePrimaryKeyAsync()
        {
            bool alreadyOpened = connection.State == ConnectionState.Open;

            try
            {
                using (var command = BuildPkCommand())
                {
                    if (!alreadyOpened)
                        await connection.OpenAsync().ConfigureAwait(false);

                    if (transaction != null)
                        command.Transaction = transaction;

                    command.Connection = connection;
                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);

                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during Create Pk Command : {ex}");
                throw;
            }
            finally
            {
                if (!alreadyOpened && connection.State != ConnectionState.Closed)
                    connection.Close();
            }

        }
 
        private SqlCommand BuildTableCommand()
        {
            var stringBuilder = new StringBuilder($"CREATE TABLE {tableName.Schema().Quoted().ToString()} (");
            string empty = string.Empty;
            stringBuilder.AppendLine();
            foreach (var column in this.tableDescription.Columns)
            {
                var columnName = ParserName.Parse(column).Quoted().ToString();

                var columnTypeString = this.sqlDbMetadata.TryGetOwnerDbTypeString(column.OriginalDbType, column.GetDbType(), false, false, column.MaxLength, this.tableDescription.OriginalProvider, SqlSyncProvider.ProviderType);
                var columnPrecisionString = this.sqlDbMetadata.TryGetOwnerDbTypePrecision(column.OriginalDbType, column.GetDbType(), false, false, column.MaxLength, column.Precision, column.Scale, this.tableDescription.OriginalProvider, SqlSyncProvider.ProviderType);
                var columnType = $"{columnTypeString} {columnPrecisionString}";
                var identity = string.Empty;

                if (column.IsAutoIncrement)
                {
                    var s = column.GetAutoIncrementSeedAndStep();
                    identity = $"IDENTITY({s.Seed},{s.Step})";
                }
                var nullString = column.AllowDBNull ? "NULL" : "NOT NULL";

                // if we have a computed column, we should allow null
                if (column.IsReadOnly)
                    nullString = "NULL";

                string defaultValue = string.Empty;
                if (this.tableDescription.OriginalProvider == SqlSyncProvider.ProviderType)
                {
                    if (!string.IsNullOrEmpty(column.DefaultValue))
                    {
                        defaultValue = "DEFAULT " + column.DefaultValue;
                    }
                }

                stringBuilder.AppendLine($"\t{empty}{columnName} {columnType} {identity} {nullString} {defaultValue}");
                empty = ",";
            }
            stringBuilder.Append(")");
            string createTableCommandString = stringBuilder.ToString();
            return new SqlCommand(createTableCommandString);
        }

        private SqlCommand BuildDeleteTableCommand() 
            => new SqlCommand($"ALTER TABLE {tableName.Schema().Quoted().ToString()} NOCHECK CONSTRAINT ALL; DROP TABLE {tableName.Schema().Quoted().ToString()};");

        public async Task CreateSchemaAsync()
        {
            if (string.IsNullOrEmpty(tableName.SchemaName) || tableName.SchemaName.ToLowerInvariant() == "dbo")
                return;

            bool alreadyOpened = connection.State == ConnectionState.Open;

            try
            {
                var schemaCommand = $"Create Schema {tableName.SchemaName}";

                using (var command = new SqlCommand(schemaCommand))
                {
                    if (!alreadyOpened)
                        await connection.OpenAsync().ConfigureAwait(false);

                    if (transaction != null)
                        command.Transaction = transaction;

                    command.Connection = connection;
                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);

                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during CreateTable : {ex}");
                throw;

            }
            finally
            {
                if (!alreadyOpened && connection.State != ConnectionState.Closed)
                    connection.Close();

            }
        }

        public async Task CreateTableAsync()
        {
            bool alreadyOpened = connection.State == ConnectionState.Open;

            try
            {
                using (var command = BuildTableCommand())
                {
                    if (!alreadyOpened)
                        await connection.OpenAsync().ConfigureAwait(false);

                    if (transaction != null)
                        command.Transaction = transaction;

                    command.Connection = connection;
                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);

                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during CreateTable : {ex}");
                throw;

            }
            finally
            {
                if (!alreadyOpened && connection.State != ConnectionState.Closed)
                    connection.Close();

            }

        }

        public async Task DropTableAsync()
        {
            bool alreadyOpened = connection.State == ConnectionState.Open;

            try
            {
                using (var command = BuildDeleteTableCommand())
                {
                    if (!alreadyOpened)
                        await connection.OpenAsync().ConfigureAwait(false);

                    if (transaction != null)
                        command.Transaction = transaction;

                    command.Connection = connection;
                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);

                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during DeleteTable : {ex}");
                throw;

            }
            finally
            {
                if (!alreadyOpened && connection.State != ConnectionState.Closed)
                    connection.Close();

            }

        }

        /// <summary>
        /// Check if we need to create the table in the current database
        /// </summary>
        public async Task<bool> NeedToCreateTableAsync()
        {
            return !await SqlManagementUtils.TableExistsAsync(connection, transaction, tableName.Schema().Quoted().ToString()).ConfigureAwait(false);

        }

        /// <summary>
        /// Check if we need to create the table in the current database
        /// </summary>
        public async Task<bool> NeedToCreateSchemaAsync()
        {
            if (string.IsNullOrEmpty(tableName.SchemaName) || tableName.SchemaName.ToLowerInvariant() == "dbo")
                return false;

            return !await SqlManagementUtils.SchemaExistsAsync(connection, transaction, tableName.SchemaName).ConfigureAwait(false);
        }


    }
}
