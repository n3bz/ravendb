﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Raven.Database.Data;
using System.Diagnostics;
using System.Threading;
using System.Security.Cryptography;
using Raven.Database.Indexing;

namespace Raven.Database
{
    public class DynamicQueryRunner
    {
        private DocumentDatabase documentDatabase;
        private Dictionary<string, TemporaryIndexInfo> temporaryIndexes;
        private DateTime lastCleanup;

        public DynamicQueryRunner(DocumentDatabase database)
        {
            documentDatabase = database;
            temporaryIndexes = new Dictionary<string, TemporaryIndexInfo>();
            lastCleanup = DateTime.Now;
        }

        public QueryResult ExecuteDynamicQuery(IndexQuery query)
        {
            // Create the map
            var map = DynamicQueryMapping.Create(query.Query);

            // Get the index name
            string indexName = FindDynamicIndexName(map);

            // Re-write the query
            string realQuery = query.Query;
            foreach (var mapItem in map.Items)
            {
                realQuery = realQuery.Replace(mapItem.From, mapItem.To);
            }

            // Perform the query until we have some results at least
            QueryResult result = null;
            var sp = Stopwatch.StartNew();
            while (true)
            {
                result = documentDatabase.Query(indexName,
                   new Raven.Database.Data.IndexQuery()
                   {
                       Cutoff = query.Cutoff,
                       PageSize = query.PageSize,
                       Query = realQuery,
                       Start = query.Start,
                       FieldsToFetch = query.FieldsToFetch,
                       SortedFields = query.SortedFields,
                   });

                if (result.IsStale && result.Results.Count < query.PageSize)
                {
                    if (sp.Elapsed.TotalMilliseconds > 10000)
                    {
                        sp.Stop();
                        break;
                    }

                    Thread.Sleep(100);
                    continue;
                }
                else
                {
                    break;
                }
            }

            return result;
        }

        private string FindDynamicIndexName(DynamicQueryMapping map)
        {
            String combinedFields = String.Join("",
                map.Items
                .OrderBy(x => x.To)
                .Select(x => x.To)
                .ToArray());
            var indexName = combinedFields;

            // Hash the name if it's too long
            if (indexName.Length > 240)
            {
                using (var sha256 = SHA256.Create())
                {
                    var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(indexName));
                    indexName = Encoding.UTF8.GetString(bytes);
                }
            }

            String permanentIndexName = string.Format("Auto_{0}", indexName);
            String temporaryIndexName = string.Format("Temp_{0}", indexName);

            // If there is a permanent index, then use that without bothering anything else
            var permanentIndex = documentDatabase.GetIndexDefinition(permanentIndexName);
            if (permanentIndex != null) { return permanentIndexName; }

            // Else head down the temporary route
            return TouchTemporaryIndex(map, temporaryIndexName, permanentIndexName);           
        }

        private string TouchTemporaryIndex(DynamicQueryMapping map, string temporaryIndexName, string permanentIndexName)
        {
            var indexInfo = GetOrAddTemporaryIndexInfo(temporaryIndexName);
            indexInfo.LastRun = DateTime.Now;
            indexInfo.RunCount++;
            
            if (TemporaryIndexShouldBeMadePermanent(indexInfo))
            {
                documentDatabase.DeleteIndex(temporaryIndexName);
                CreateIndex(map, permanentIndexName);
                temporaryIndexes.Remove(temporaryIndexName);
                return permanentIndexName;
            }
            else
            {
                var temporaryIndex = documentDatabase.GetIndexDefinition(temporaryIndexName);
                if (temporaryIndex != null) { return temporaryIndexName; }
                CreateIndex(map, temporaryIndexName);
                return temporaryIndexName;
            }
       }

        private bool TemporaryIndexShouldBeMadePermanent(TemporaryIndexInfo indexInfo)
        {
            // Too small a sample
            if (indexInfo.RunCount < 100) { return false; }

            var timeSinceCreation = DateTime.Now.Subtract(indexInfo.Created);
            var score = timeSinceCreation.TotalMilliseconds / indexInfo.RunCount;

            if (score < 6000) return true; // 100 times in 60000 milliseconds (10 minutes)
            return false;
        }

        private TemporaryIndexInfo GetOrAddTemporaryIndexInfo(string temporaryIndexName)
        {
            TemporaryIndexInfo info = null;
            if (!temporaryIndexes.TryGetValue(temporaryIndexName, out info))
            {
                info = new TemporaryIndexInfo()
                {
                    Created = DateTime.Now,
                    RunCount = 0,
                    Name = temporaryIndexName
                };
                temporaryIndexes[temporaryIndexName] = info;
            }
            return info;
        }

        private void CreateIndex(DynamicQueryMapping map, string indexName)
        {
            // Create the index
            var mapping = map.Items
              .Select(x => string.Format("{0} = doc.{1}", x.To, x.From))
              .ToArray();

            var indexes = new Dictionary<string, FieldIndexing>();
            foreach (var mapItem in map.Items)
            {
                indexes.Add(mapItem.To, FieldIndexing.NotAnalyzed);
            }

            // Create the definition
            var definition = new IndexDefinition()
            {
                Map = @"from doc in docs select new 
                 { 
                    " + String.Join(",\r\n", mapping) + @"
                 }",
                Indexes = indexes
            };

            documentDatabase.PutIndex(indexName, definition);
        }

        private class TemporaryIndexInfo
        {
            public string Name { get; set;}
            public DateTime LastRun { get; set;}
            public DateTime Created { get; set;}
            public int RunCount { get; set;}
        }
    }
}