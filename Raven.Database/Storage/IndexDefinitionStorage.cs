//-----------------------------------------------------------------------
// <copyright file="IndexDefinitionStorage.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Microsoft.Isam.Esent.Interop;
using Mono.CSharp;
using Mono.CSharp.Linq;
using Raven.Abstractions.Data;
using Raven.Abstractions.Logging;
using Raven.Database.Indexing.IndexMerging;
using Raven.Imports.Newtonsoft.Json;
using Raven.Abstractions;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Indexing;
using Raven.Abstractions.MEF;
using Raven.Database.Config;
using Raven.Database.Extensions;
using Raven.Database.Linq;
using Raven.Database.Plugins;
using Raven.Database.Indexing;

namespace Raven.Database.Storage
{
    public class IndexDefinitionStorage
    {
        private const string IndexDefDir = "IndexDefinitions";

        private readonly ReaderWriterLockSlim currentlyIndexingLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
        public long currentlyIndexing;

        private readonly ConcurrentDictionary<int, AbstractViewGenerator> indexCache =
            new ConcurrentDictionary<int, AbstractViewGenerator>();

        private readonly ConcurrentDictionary<int, AbstractTransformer> transformCache =
            new ConcurrentDictionary<int, AbstractTransformer>();

        private readonly ConcurrentDictionary<string, int> indexNameToId =
            new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, int> transformNameToId =
            new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<int, TransformerDefinition> transformDefinitions =
            new ConcurrentDictionary<int, TransformerDefinition>();

        private readonly ConcurrentDictionary<int, IndexDefinition> indexDefinitions =
            new ConcurrentDictionary<int, IndexDefinition>();

        private readonly ConcurrentDictionary<int, IndexDefinition> newDefinitionsThisSession =
            new ConcurrentDictionary<int, IndexDefinition>();

        private static readonly ILog logger = LogManager.GetCurrentClassLogger();
        private readonly string path;
        private readonly InMemoryRavenConfiguration configuration;

        private readonly ITransactionalStorage transactionalStorage;

        private readonly OrderedPartCollection<AbstractDynamicCompilationExtension> extensions;


        [CLSCompliant(false)]
        public IndexDefinitionStorage(
            InMemoryRavenConfiguration configuration,
            ITransactionalStorage transactionalStorage,
            string path,
            OrderedPartCollection<AbstractDynamicCompilationExtension> extensions)
        {
            this.configuration = configuration;
            this.transactionalStorage = transactionalStorage;
            this.extensions = extensions; // this is used later in the ctor, so it must appears first
            this.path = Path.Combine(path, IndexDefDir);

            if (Directory.Exists(this.path) == false && configuration.RunInMemory == false)
                Directory.CreateDirectory(this.path);

            if (configuration.RunInMemory == false)
                ReadFromDisk();

            newDefinitionsThisSession.Clear();
        }

        public bool IsNewThisSession(IndexDefinition definition)
        {
            return newDefinitionsThisSession.ContainsKey(definition.IndexId);
        }

        private void ReadFromDisk()
        {
            foreach (var indexDefinition in ReadIndexDefinitionsFromDisk())
            {
                try
                {
                    if (Contains(indexDefinition.Name)) // checking if there are no older indexes with the same name
                        RemoveIndexAndCleanup(indexDefinition.Name);

                    ResolveAnalyzers(indexDefinition);
                    AddAndCompileIndex(indexDefinition);
                    AddIndex(indexDefinition.IndexId, indexDefinition);
                }
                catch (Exception e)
                {
                    logger.WarnException(string.Format("Could not compile index '{0} ({1})', skipping bad index", indexDefinition.IndexId, indexDefinition.Name), e);
                }
            }

            foreach (var transformerDefinition in ReadTransformerDefinitionsFromDisk())
            {
                try
                {
                    RemoveTransformer(transformerDefinition.Name);

                    var generator = CompileTransform(transformerDefinition);
                    transformCache[transformerDefinition.TransfomerId] = generator;
                    AddTransform(transformerDefinition.TransfomerId, transformerDefinition);
                }
                catch (Exception e)
                {
                    logger.WarnException(string.Format("Could not compile transformer '{0} ({1})', skipping bad transformer", transformerDefinition.TransfomerId, transformerDefinition.Name), e);
                }
            }
        }

        private IEnumerable<IndexDefinition> ReadIndexDefinitionsFromDisk()
        {
            var result = new SortedList<int, IndexDefinition>();

            foreach (var index in Directory.GetFiles(path, "*.index"))
            {
                try
                {
                    var indexDefinition = JsonConvert.DeserializeObject<IndexDefinition>(File.ReadAllText(index), Default.Converters);
                    result.Add(indexDefinition.IndexId, indexDefinition);
                }
                catch (Exception e)
                {
                    logger.WarnException("Could not compile index " + index + ", skipping bad index", e);
                }
            }

            return result.Values;
        }

        private IEnumerable<TransformerDefinition> ReadTransformerDefinitionsFromDisk()
        {
            var result = new SortedList<int, TransformerDefinition>();

            foreach (var transformer in Directory.GetFiles(path, "*.transform"))
            {
                try
                {
                    var transformerDefinition = JsonConvert.DeserializeObject<TransformerDefinition>(File.ReadAllText(transformer), Default.Converters);

                    result.Add(transformerDefinition.TransfomerId, transformerDefinition);
                }
                catch (Exception e)
                {
                    logger.WarnException("Could not compile transformer " + transformer + ", skipping bad transformer", e);
                }
            }

            return result.Values;
        }

        private void RemoveIndexAndCleanup(string name)
        {
            var index = GetIndexDefinition(name);
            if (index == null) 
                return;

            transactionalStorage.Batch(accessor =>
            {
                accessor.Indexing.PrepareIndexForDeletion(index.IndexId);
                accessor.Indexing.DeleteIndex(index.IndexId, CancellationToken.None);
            });

            RemoveIndex(index.IndexId);
        }

        public int IndexesCount
        {
            get { return indexCache.Count; }
        }

        public int ResultTransformersCount
        {
            get { return transformCache.Count; }
        }

        public string[] IndexNames
        {
            get { return IndexDefinitions.Values.OrderBy(x => x.Name).Select(x => x.Name).ToArray(); }
        }

        public int[] Indexes
        {
            get { return indexCache.Keys.OrderBy(name => name).ToArray(); }
        }

        public string IndexDefinitionsPath
        {
            get { return path; }
        }

        public string[] TransformerNames
        {
            get
            {
                return transformDefinitions.Values
                                           .OrderBy(x => x.Name)
                                           .Select(x => x.Name)
                                           .ToArray();
            }
        }

        public ConcurrentDictionary<int, IndexDefinition> IndexDefinitions
        {
            get { return indexDefinitions; }
        }

        public string CreateAndPersistIndex(IndexDefinition indexDefinition)
        {
            var transformer = AddAndCompileIndex(indexDefinition);
            if (configuration.RunInMemory == false)
            {
                WriteIndexDefinition(indexDefinition);
            }
            return transformer.Name;
        }

        private void WriteIndexDefinition(IndexDefinition indexDefinition)
        {
            if (configuration.RunInMemory)
                return;
            var indexName = Path.Combine(path, indexDefinition.IndexId + ".index");
            File.WriteAllText(indexName,
                              JsonConvert.SerializeObject(indexDefinition, Formatting.Indented, Default.Converters));
        }

        private void WriteTransformerDefinition(TransformerDefinition transformerDefinition)
        {
            if (configuration.RunInMemory)
                return;

            string transformerFileName = Path.Combine(path, transformerDefinition.TransfomerId + ".transform");

            File.WriteAllText(transformerFileName,
                              JsonConvert.SerializeObject(transformerDefinition, Formatting.Indented, Default.Converters));
        }

        [CLSCompliant(false)]
        public string CreateAndPersistTransform(TransformerDefinition transformerDefinition, AbstractTransformer transformer)
        {
            transformCache.AddOrUpdate(transformerDefinition.TransfomerId, transformer, (s, viewGenerator) => transformer);
            if (configuration.RunInMemory == false)
            {
                WriteTransformerDefinition(transformerDefinition);
            }
            return transformer.Name;
        }

        public void UpdateIndexDefinitionWithoutUpdatingCompiledIndex(IndexDefinition definition)
        {
            IndexDefinitions.AddOrUpdate(definition.IndexId, s =>
            {
                throw new InvalidOperationException(
                    "Cannot find index named: " +
                    definition.IndexId);
            }, (s, indexDefinition) => definition);
            WriteIndexDefinition(definition);
        }

        private DynamicViewCompiler AddAndCompileIndex(IndexDefinition indexDefinition)
        {
            var transformer = new DynamicViewCompiler(indexDefinition.Name, indexDefinition, extensions, path,
                                                      configuration);
            var generator = transformer.GenerateInstance();
            indexCache.AddOrUpdate(indexDefinition.IndexId, generator, (s, viewGenerator) => generator);

            logger.Info("New index {0}:\r\n{1}\r\nCompiled to:\r\n{2}", transformer.Name, transformer.CompiledQueryText,
                        transformer.CompiledQueryText);
            return transformer;
        }

        [CLSCompliant(false)]
        public AbstractTransformer CompileTransform(TransformerDefinition transformerDefinition)
        {
            var transformer = new DynamicTransformerCompiler(transformerDefinition, configuration, extensions,
                                                             transformerDefinition.Name, path);
            var generator = transformer.GenerateInstance();

            logger.Info("New transformer {0}:\r\n{1}\r\nCompiled to:\r\n{2}", transformer.Name,
                        transformer.CompiledQueryText,
                        transformer.CompiledQueryText);
            return generator;
        }

        public void RegisterNewIndexInThisSession(string name, IndexDefinition definition)
        {
            newDefinitionsThisSession.TryAdd(definition.IndexId, definition);
        }

        public void AddIndex(int id, IndexDefinition definition)
        {
            IndexDefinitions.AddOrUpdate(id, definition, (s1, def) =>
            {
                if (def.IsCompiled)
                    throw new InvalidOperationException("Index " + id + " is a compiled index, and cannot be replaced");
                return definition;
            });
            indexNameToId[definition.Name] = id;

            UpdateIndexMappingFile();
        }

        public void AddTransform(int id, TransformerDefinition definition)
        {
            transformDefinitions.AddOrUpdate(id, definition, (s1, def) => definition);
            transformNameToId[definition.Name] = id;

            UpdateTransformerMappingFile();
        }

        public void RemoveIndex(int id, bool removeByNameMapping = true)
        {
            AbstractViewGenerator ignoredViewGenerator;
            int ignoredId;
            if (indexCache.TryRemove(id, out ignoredViewGenerator) && removeByNameMapping)
                indexNameToId.TryRemove(ignoredViewGenerator.Name, out ignoredId);
            IndexDefinition ignoredIndexDefinition;
            IndexDefinitions.TryRemove(id, out ignoredIndexDefinition);
            newDefinitionsThisSession.TryRemove(id, out ignoredIndexDefinition);
            if (configuration.RunInMemory)
                return;
            File.Delete(GetIndexSourcePath(id) + ".index");
            UpdateIndexMappingFile();
        }

        private string GetIndexSourcePath(int id)
        {
            return Path.Combine(path, id.ToString());
        }

        public IndexDefinition GetIndexDefinition(string name)
        {
            int id = 0;
            if (indexNameToId.TryGetValue(name, out id))
            {
                IndexDefinition indexDefinition = IndexDefinitions[id];
                if (indexDefinition.Fields.Count == 0)
                {
                    AbstractViewGenerator abstractViewGenerator = GetViewGenerator(id);
                    indexDefinition.Fields = abstractViewGenerator.Fields;
                }
                return indexDefinition;
            }
            return null;
        }

        public IndexDefinition GetIndexDefinition(int id)
        {
            IndexDefinition value;
            IndexDefinitions.TryGetValue(id, out value);
            return value;
        }

        public TransformerDefinition GetTransformerDefinition(string name)
        {
            int id;
            if (transformNameToId.TryGetValue(name, out id))
                return transformDefinitions[id];
            return null;
        }

        public IEnumerable<TransformerDefinition> GetAllTransformerDefinitions()
        {
            return transformDefinitions.Select(definition => definition.Value);
        }

        public IndexMergeResults ProposeIndexMergeSuggestions()
        {
            var indexMerger = new IndexMerger(IndexDefinitions.ToDictionary(x=>x.Key,x=>x.Value));
            return indexMerger.ProposeIndexMergeSuggestions();
        }

        public TransformerDefinition GetTransformerDefinition(int id)
        {
            TransformerDefinition value;
            transformDefinitions.TryGetValue(id, out value);
            return value;
        }

        [CLSCompliant(false)]
        public AbstractViewGenerator GetViewGenerator(string name)
        {
            int id = 0;
            if (indexNameToId.TryGetValue(name, out id))
                return indexCache[id];
            return null;
        }

        [CLSCompliant(false)]
        public AbstractViewGenerator GetViewGenerator(int id)
        {
            AbstractViewGenerator value;
            if (indexCache.TryGetValue(id, out value) == false)
                return null;
            return value;
        }

        public IndexCreationOptions FindIndexCreationOptions(IndexDefinition newIndexDef)
        {
            var currentIndexDefinition = GetIndexDefinition(newIndexDef.Name);
            if (currentIndexDefinition == null)
                return IndexCreationOptions.Create;

            if (currentIndexDefinition.IsTestIndex) // always update test indexes
                return IndexCreationOptions.Update;

            newIndexDef.IndexId = currentIndexDefinition.IndexId;
            var result = currentIndexDefinition.Equals(newIndexDef);
            if (result)
                return IndexCreationOptions.Noop;

            // try to compare to find changes which doesn't require removing compiled index
            return currentIndexDefinition.Equals(newIndexDef, ignoreFormatting: true, ignoreMaxIndexOutput: true)
                ? IndexCreationOptions.UpdateWithoutUpdatingCompiledIndex : IndexCreationOptions.Update;
        }

        public bool Contains(string indexName)
        {
            return indexNameToId.ContainsKey(indexName);
        }

        public static void ResolveAnalyzers(IndexDefinition indexDefinition)
        {
            // Stick Lucene.Net's namespace to all analyzer aliases that are missing a namespace
            var analyzerNames = (from analyzer in indexDefinition.Analyzers
                                 where analyzer.Value.IndexOf('.') == -1
                                 select analyzer).ToArray();

            // Only do this for analyzer that actually exist; we do this here to be able to throw a correct error later on
            foreach (
                var a in
                    analyzerNames.Where(
                        a => typeof(StandardAnalyzer).Assembly.GetType("Lucene.Net.Analysis." + a.Value) != null))
            {
                indexDefinition.Analyzers[a.Key] = "Lucene.Net.Analysis." + a.Value;
            }
        }

        public IDisposable TryRemoveIndexContext()
        {
            if (currentlyIndexingLock.TryEnterWriteLock(TimeSpan.FromSeconds(60)) == false)
                throw new InvalidOperationException(
                    "Cannot modify indexes while indexing is in progress (already waited full minute). Try again later");
            return new DisposableAction(currentlyIndexingLock.ExitWriteLock);
        }


        public bool IsCurrentlyIndexing()
        {
            return Interlocked.Read(ref currentlyIndexing) != 0;
        }

        [CLSCompliant(false)]
        public IDisposable CurrentlyIndexing()
        {
            currentlyIndexingLock.EnterReadLock();
            Interlocked.Increment(ref currentlyIndexing);
            return new DisposableAction(() =>
            {
                currentlyIndexingLock.ExitReadLock();
                Interlocked.Decrement(ref currentlyIndexing);
            });
        }

        public bool RemoveTransformer(string name)
        {
            var transformer = GetTransformerDefinition(name);
            if (transformer == null) 
                return false;
            RemoveTransformer(transformer.TransfomerId);

            return true;
        }

        public void RemoveIndex(string name)
        {
            var index = GetIndexDefinition(name);
            if (index == null) return;
            RemoveIndex(index.IndexId);
        }

        public void RemoveTransformer(int id)
        {
            AbstractTransformer ignoredViewGenerator;
            int ignoredId;
            if (transformCache.TryRemove(id, out ignoredViewGenerator))
                transformNameToId.TryRemove(ignoredViewGenerator.Name, out ignoredId);
            TransformerDefinition ignoredIndexDefinition;
            transformDefinitions.TryRemove(id, out ignoredIndexDefinition);
            if (configuration.RunInMemory)
                return;
            File.Delete(GetIndexSourcePath(id) + ".transform");
            UpdateTransformerMappingFile();
        }

        [CLSCompliant(false)]
        public AbstractTransformer GetTransformer(int id)
        {
            AbstractTransformer value;
            if (transformCache.TryGetValue(id, out value) == false)
                return null;
            return value;
        }

        [CLSCompliant(false)]
        public AbstractTransformer GetTransformer(string name)
        {
            int id = 0;
            if (transformNameToId.TryGetValue(name, out id))
                return transformCache[id];
            return null;
        }

        internal bool ReplaceIndex(string indexName, string indexToSwapName)
        {
            var index = GetIndexDefinition(indexName);
            if (index == null)
                return false;

            int _;
            indexNameToId.TryRemove(index.Name, out _);
            index.IsSideBySideIndex = false;

            var indexToReplace = GetIndexDefinition(indexToSwapName);
            index.Name = indexToReplace != null ? indexToReplace.Name : indexToSwapName;
            CreateAndPersistIndex(index);
            AddIndex(index.IndexId, index);

            return true;
        }

        private void UpdateIndexMappingFile()
        {
            if (configuration.RunInMemory)
                return;

            var sb = new StringBuilder();

            foreach (var index in indexNameToId)
            {
                sb.Append(string.Format("{0} - {1}{2}", index.Value, index.Key, Environment.NewLine));
            }

            File.WriteAllText(Path.Combine(path, "indexes.txt"), sb.ToString());
        }

        private void UpdateTransformerMappingFile()
        {
            if (configuration.RunInMemory)
                return;

            var sb = new StringBuilder();

            foreach (var transform in transformNameToId)
            {
                sb.Append(string.Format("{0} - {1}{2}", transform.Value, transform.Key, Environment.NewLine));
            }

            File.WriteAllText(Path.Combine(path, "transformers.txt"), sb.ToString());
        }

        public void UpdateTransformerDefinitionWithoutUpdatingCompiledTransformer(TransformerDefinition definition)
        {
            transformDefinitions.AddOrUpdate(definition.TransfomerId, s =>
            {
                throw new InvalidOperationException(
                    "Cannot find transformer named: " +
                    definition.TransfomerId);
            }, (s, transformerDefinition) => definition);
            WriteTransformerDefinition(definition);
        }
    }

}
