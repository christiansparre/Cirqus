﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using d60.Cirqus.Events;
using d60.Cirqus.Extensions;
using d60.Cirqus.Logging;
using d60.Cirqus.Views.ViewManagers;
using d60.Cirqus.Views.ViewManagers.New;

namespace d60.Cirqus.MsSql.Views
{
    public class NewMsSqlViewManager<TViewInstance> : IManagedView<TViewInstance> where TViewInstance : class, IViewInstance, ISubscribeTo, new()
    {
        static Logger _logger;

        static NewMsSqlViewManager()
        {
            CirqusLoggerFactory.Changed += f => _logger = f.GetCurrentClassLogger();
        }

        const int PrimaryKeySize = 100;
        const int DefaultLowWatermark = -1;

        readonly ViewDispatcherHelper<TViewInstance> _dispatcher = new ViewDispatcherHelper<TViewInstance>();
        readonly string _connectionString;
        readonly string _tableName;
        readonly Prop[] _schema;

        long _cachedLowWatermark;

        public NewMsSqlViewManager(string connectionStringOrConnectionStringName, string tableName, bool automaticallyCreateSchema = true)
        {
            _connectionString = SqlHelper.GetConnectionString(connectionStringOrConnectionStringName);
            _tableName = tableName;
            _schema = SchemaHelper.GetSchema<TViewInstance>();

            if (automaticallyCreateSchema)
            {
                CreateSchema();
            }
        }

        public NewMsSqlViewManager(string connectionString, bool automaticallyCreateSchema = true)
            : this(connectionString, typeof(TViewInstance).Name, automaticallyCreateSchema)
        {
        }

        public long GetLowWatermark(bool canGetFromCache = true)
        {
            if (canGetFromCache && false)
            {
                return GetLowWatermarkFromMemory()
                       ?? GetLowWatermarkFromDb()
                       ?? DefaultLowWatermark;
            }

            return GetLowWatermarkFromDb()
                   ?? DefaultLowWatermark;
        }

        long? GetLowWatermarkFromMemory()
        {
            var value = Interlocked.Read(ref _cachedLowWatermark);

            if (value != DefaultLowWatermark)
            {
                return value;
            }

            return null;
        }

        long? GetLowWatermarkFromDb()
        {
            using (var conn = new SqlConnection(_connectionString))
            {
                conn.Open();

                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = string.Format("SELECT MAX([LastGlobalSequenceNumber]) FROM [{0}]", _tableName);

                        var result = cmd.ExecuteScalar();

                        if (result != DBNull.Value)
                        {
                            return (long)result;
                        }
                    }
                }
            }

            return null;
        }

        public void Dispatch(IViewContext viewContext, IEnumerable<DomainEvent> batch)
        {
            var eventList = batch.ToList();

            if (!eventList.Any()) return;

            using (var conn = new SqlConnection(_connectionString))
            {
                conn.Open();

                using (var tx = conn.BeginTransaction())
                {
                    var locator = ViewLocator.GetLocatorFor<TViewInstance>();
                    var activeViewsById = new Dictionary<string, TViewInstance>();

                    foreach (var e in eventList)
                    {
                        if (!ViewLocator.IsRelevant<TViewInstance>(e)) continue;

                        var viewIds = locator.GetVirewIds(viewContext, e);

                        foreach (var viewId in viewIds)
                        {
                            var view = activeViewsById
                                .GetOrAdd(viewId, id => FindOneById(id, tx, conn)
                                                        ?? _dispatcher.CreateNewInstance(viewId));

                            _dispatcher.DispatchToView(viewContext, e, view);
                        }
                    }

                    Save(activeViewsById, conn, tx);

                    tx.Commit();
                }
            }

            Interlocked.Exchange(ref _cachedLowWatermark, eventList.Max(e => e.GetGlobalSequenceNumber()));
        }

        public async Task WaitUntilProcessed(CommandProcessingResult result, TimeSpan timeout)
        {
            if (!result.EventsWereEmitted) return;

            var mostRecentGlobalSequenceNumber = result.GlobalSequenceNumbersOfEmittedEvents.Max();

            var stopwatch = Stopwatch.StartNew();

            while (GetLowWatermark(canGetFromCache: false) < mostRecentGlobalSequenceNumber)
            {
                if (stopwatch.Elapsed > timeout)
                {
                    throw new TimeoutException(string.Format("View for {0} did not catch up to {1} within {2} timeout!",
                        typeof(TViewInstance), mostRecentGlobalSequenceNumber, timeout));
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10));
            }
        }

        public TViewInstance Load(string viewId)
        {
            using (var conn = new SqlConnection(_connectionString))
            {
                conn.Open();

                using (var tx = conn.BeginTransaction())
                {
                    return FindOneById(viewId, tx, conn);
                }
            }
        }

        public void Purge()
        {
            _logger.Info("Purging SQL Server table {0}", _tableName);

            using (var conn = new SqlConnection(_connectionString))
            {
                conn.Open();

                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = string.Format(@"DELETE FROM [{0}]", _tableName);
                        cmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                }
            }

            Interlocked.Exchange(ref _cachedLowWatermark, DefaultLowWatermark);
        }

        TViewInstance FindOneById(string viewId, SqlTransaction tx, SqlConnection conn)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = string.Format(@"

SELECT 

{0}

FROM [{1}] WHERE [Id] = @id

", FormatColumnNames(_schema), _tableName);

                cmd.Parameters.Add("Id", SqlDbType.Char, PrimaryKeySize).Value = viewId;

                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        var view = _dispatcher.CreateNewInstance(viewId);

                        foreach (var prop in _schema)
                        {
                            prop.Setter(view, reader[prop.ColumnName]);
                        }

                        return view;
                    }

                    return null;
                }
            }
        }

        static string FormatAssignments(IEnumerable<Prop> schema)
        {
            return string.Join(", " + Environment.NewLine,
                schema.Select(prop => string.Format("[{0}] = {1}", prop.ColumnName, prop.SqlParameterName)));
        }

        string FormatParameterNames(IEnumerable<Prop> schema)
        {
            return string.Join(", " + Environment.NewLine,
                schema.Select(prop => prop.SqlParameterName));
        }

        static string FormatColumnNames(IEnumerable<Prop> schema)
        {
            return string.Join(", " + Environment.NewLine,
                schema.Select(prop => string.Format("[{0}]", prop.ColumnName)));
        }

        void Save(Dictionary<string, TViewInstance> activeViewsById, SqlConnection conn, SqlTransaction tx)
        {
            _logger.Debug("Flushing {0} view instances to '{1}'", activeViewsById.Count, _tableName);

            foreach (var kvp in activeViewsById)
            {
                var id = kvp.Key;
                var view = kvp.Value;

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = string.Format(@"

MERGE [{0}] AS ViewTable

USING (VALUES (@Id)) AS foo(Id)

ON ViewTable.Id = foo.Id

WHEN MATCHED THEN

    UPDATE SET 
{1}

WHEN NOT MATCHED THEN

    INSERT (
{2}
) VALUES (
{3}
)
    
;
", _tableName, FormatAssignments(_schema.Where(prop => !prop.IsPrimaryKey)), FormatColumnNames(_schema), FormatParameterNames(_schema));

                    cmd.Parameters.Add("Id", SqlDbType.NChar, PrimaryKeySize).Value = id;

                    foreach (var prop in _schema.Where(p => !p.IsPrimaryKey))
                    {
                        var value = prop.Getter(view);

                        cmd.Parameters.AddWithValue(prop.SqlParameterName, value ?? DBNull.Value);
                    }

                    cmd.ExecuteNonQuery();
                }
            }
        }

        void CreateSchema()
        {
            _logger.Info("Ensuring that schema for '{0}' is created...", typeof(TViewInstance));

            using (var conn = new SqlConnection(_connectionString))
            {
                conn.Open();

                if (!SchemaLooksRight(conn, _tableName))
                {
                    DropTable(conn, _tableName);
                }

                CreateTable(conn, _tableName);
            }
        }

        bool SchemaLooksRight(SqlConnection conn, string tableName)
        {
            var columnsPresentInDatabase = new List<Prop>();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = string.Format(@"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = '{0}'", tableName);

                var tableExists = Convert.ToInt32(cmd.ExecuteScalar()) > 0;

                if (!tableExists)
                {
                    return true;
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = string.Format(@"SELECT COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{0}'", tableName);

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var columnName = (string)reader["COLUMN_NAME"];
                        var dataType = (string)reader["DATA_TYPE"];

                        var sqlDbType = (SqlDbType)Enum.Parse(typeof(SqlDbType), dataType, true);

                        columnsPresentInDatabase.Add(new Prop
                        {
                            ColumnName = columnName,
                            SqlDbType = sqlDbType
                        });
                    }
                }
            }

            var columnsPresentInSchemaMissingOrWithWrongTypeInDatabase = _schema
                .Where(schemaColumn => !columnsPresentInDatabase.Any(c => c.Matches(schemaColumn)))
                .ToList();

            if (columnsPresentInSchemaMissingOrWithWrongTypeInDatabase.Any())
            {
                _logger.Warn("The table '{0}' is missing columns with the following names and types: {1}",
                    tableName, string.Join(", ", columnsPresentInSchemaMissingOrWithWrongTypeInDatabase.Select(prop => string.Format("{0} ({1})", prop.ColumnName, prop.SqlDbType))));
                return false;
            }

            return true;
        }

        void DropTable(SqlConnection conn, string tableName)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = string.Format(@"
IF EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = '{0}')
BEGIN
    DROP TABLE [{0}]
END
", tableName);
                cmd.ExecuteNonQuery();
            }
        }

        void CreateTable(SqlConnection conn, string tableName)
        {
            using (var cmd = conn.CreateCommand())
            {
                var script = string.Format("[Id] [NVARCHAR]({0}) NOT NULL, ", PrimaryKeySize)
                             + Environment.NewLine
                             + string.Join("," + Environment.NewLine, _schema
                                 .Where(c => !c.IsPrimaryKey)
                                 .Select(c => string.Format("[{0}] [{1}]{2} {3}",
                                     c.ColumnName,
                                     c.SqlDbType,
                                     string.IsNullOrWhiteSpace(c.Size) ? "" : "(" + c.Size + ")",
                                     c.IsNullable ? "NULL" : "NOT NULL")));

                var commandText = string.Format(@"

IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = '{0}')
BEGIN
    CREATE TABLE [dbo].[{0}] (

{1},


        CONSTRAINT [PK_{0}] PRIMARY KEY CLUSTERED 
        (
	        [id] ASC
        ) WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON)
    )
END

", tableName, script);

                cmd.CommandText = commandText;

                cmd.ExecuteNonQuery();
            }
        }
    }
}