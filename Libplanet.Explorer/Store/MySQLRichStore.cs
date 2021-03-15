using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Bencodex.Types;
using Libplanet.Action;
using Libplanet.Blocks;
using Libplanet.Store;
using Libplanet.Tx;
using LruCacheNet;
using MySqlConnector;
using MySqlStore.Models;
using Serilog;
using SqlKata;
using SqlKata.Compilers;
using SqlKata.Execution;

namespace Libplanet.Explorer.Store
{
    // It assumes running Explorer as online-mode.
    public class MySQLRichStore : IRichStore
    {
        private const string BlockDbName = "block";
        private const string TxDbName = "transaction";
        private const string TxRefDbName = "tx_references";
        private const string SignerRefDbName = "signer_references";
        private const string UpdatedAddressRefDbName = "updated_address_references";

        private readonly LruCache<HashDigest<SHA256>, BlockDigest> _blockCache;

        // FIXME we should separate it.
        private readonly IStore _store;

        private readonly MySqlCompiler _compiler;
        private readonly string _connectionString;

        public MySQLRichStore(IStore store, MySQLRichStoreOptions options)
        {
            _store = store;

            var builder = new MySqlConnectionStringBuilder
            {
                Database = options.Database,
                UserID = options.Username,
                Password = options.Password,
                Server = options.Server,
                Port = options.Port,
                AllowLoadLocalInfile = true,
            };

            _connectionString = builder.ConnectionString;
            _compiler = new MySqlCompiler();

            _blockCache = new LruCache<HashDigest<SHA256>, BlockDigest>(capacity: 512);
        }

        /// <inheritdoc cref="IStore"/>
        public long? GetBlockIndex(HashDigest<SHA256> blockHash)
        {
            return _store.GetBlockIndex(blockHash);
        }

        public DateTimeOffset? GetBlockPerceivedTime(HashDigest<SHA256> blockHash)
        {
            return _store.GetBlockPerceivedTime(blockHash);
        }

        public BlockDigest? GetBlockDigest(HashDigest<SHA256> blockHash)
        {
            if (_blockCache.TryGetValue(blockHash, out BlockDigest cachedDigest))
            {
                return cachedDigest;
            }

            var blockDigest = _store.GetBlockDigest(blockHash);

            if (!(blockDigest is null))
            {
                _blockCache.AddOrUpdate(blockHash, blockDigest.Value);
            }

            return blockDigest;
        }

        /// <inheritdoc cref="IStore"/>
        public bool DeleteBlock(HashDigest<SHA256> blockHash)
        {
            if (!Select<BlockModel, string>(BlockDbName, "hash", blockHash.ToString()).Any())
            {
                return false;
            }

            Delete(BlockDbName, "hash", blockHash.ToString());
            _blockCache.Remove(blockHash);

            _store.DeleteBlock(blockHash);
            return true;
        }

        /// <inheritdoc cref="IStore"/>
        public bool ContainsBlock(HashDigest<SHA256> blockHash)
        {
            if (_blockCache.ContainsKey(blockHash))
            {
                return true;
            }

            return _store.ContainsBlock(blockHash);
        }

        /// <inheritdoc cref="IStore"/>
        public IEnumerable<KeyValuePair<Address, long>> ListTxNonces(Guid chainId)
        {
            return _store.ListTxNonces(chainId);
        }

        /// <inheritdoc cref="IStore"/>
        public long GetTxNonce(Guid chainId, Address address)
        {
            return _store.GetTxNonce(chainId, address);
        }

        /// <inheritdoc cref="IStore"/>
        public void IncreaseTxNonce(Guid chainId, Address signer, long delta = 1)
        {
            _store.IncreaseTxNonce(chainId, signer, delta);
        }

        /// <inheritdoc cref="IStore"/>
        public bool ContainsTransaction(TxId txId)
        {
            return _store.ContainsTransaction(txId);
        }

        /// <inheritdoc cref="IStore"/>
        public long CountTransactions()
        {
            return _store.CountTransactions();
        }

        /// <inheritdoc cref="IStore"/>
        public long CountBlocks()
        {
            return _store.CountBlocks();
        }

        /// <inheritdoc cref="IStore"/>
        public void PutBlock<T>(Block<T> block)
            where T : IAction, new()
        {
            _store.PutBlock(block);
            foreach (var tx in block.Transactions)
            {
                PutTransaction(tx);
                StoreTxReferences(tx.Id, block.Hash, tx.Nonce);
            }

            string blockFilePath = Path.GetTempFileName();
            using StreamWriter blockBulkFile = new StreamWriter(blockFilePath);
            try
            {
                blockBulkFile.WriteLine(
                    $"{block.Index}," +
                    $"{block.Hash.ToString()}," +
                    $"{block.PreEvaluationHash.ToString()}," +
                    $"{block.StateRootHash?.ToString()}," +
                    $"{block.Difficulty}," +
                    $"{(long)block.TotalDifficulty}," +
                    $"{block.Nonce.ToString()}," +
                    $"{block.Miner?.ToString()}," +
                    $"{block.PreviousHash?.ToString()}," +
                    $"{block.Timestamp.ToString()}," +
                    $"{block.TxHash?.ToString()}," +
                    $"{block.ProtocolVersion}");
                blockBulkFile.Flush();
                blockBulkFile.Close();
                BulkInsert(BlockDbName, blockFilePath);
            }
            catch (Exception e)
            {
                Log.Debug(e.Message);
            }
            finally
            {
                blockBulkFile.Dispose();
            }
        }

        /// <inheritdoc cref="IStore"/>
        public IEnumerable<Guid> ListChainIds()
        {
            return _store.ListChainIds();
        }

        /// <inheritdoc cref="IStore"/>
        public void DeleteChainId(Guid chainId)
        {
            _store.DeleteChainId(chainId);
        }

        /// <inheritdoc cref="IStore"/>
        public Guid? GetCanonicalChainId()
        {
            return _store.GetCanonicalChainId();
        }

        /// <inheritdoc cref="IStore"/>
        public void SetCanonicalChainId(Guid chainId)
        {
            _store.SetCanonicalChainId(chainId);
        }

        /// <inheritdoc cref="IStore"/>
        public long CountIndex(Guid chainId)
        {
            return _store.CountIndex(chainId);
        }

        /// <inheritdoc cref="IStore"/>
        public IEnumerable<HashDigest<SHA256>> IterateIndexes(
            Guid chainId,
            int offset = 0,
            int? limit = null)
        {
            return _store.IterateIndexes(chainId, offset, limit);
        }

        /// <inheritdoc cref="IStore"/>
        public HashDigest<SHA256>? IndexBlockHash(Guid chainId, long index)
        {
            return _store.IndexBlockHash(chainId, index);
        }

        /// <inheritdoc cref="IStore"/>
        public long AppendIndex(Guid chainId, HashDigest<SHA256> hash)
        {
            return _store.AppendIndex(chainId, hash);
        }

        /// <inheritdoc cref="IStore"/>
        public void ForkBlockIndexes(
            Guid sourceChainId,
            Guid destinationChainId,
            HashDigest<SHA256> branchPoint)
        {
            _store.ForkBlockIndexes(sourceChainId, destinationChainId, branchPoint);
        }

        /// <inheritdoc cref="IStore"/>
        public void StageTransactionIds(IImmutableSet<TxId> txids)
        {
            _store.StageTransactionIds(txids);
        }

        /// <inheritdoc cref="IStore"/>
        public void UnstageTransactionIds(ISet<TxId> txids)
        {
            _store.UnstageTransactionIds(txids);
        }

        /// <inheritdoc cref="IStore"/>
        public IEnumerable<TxId> IterateStagedTransactionIds()
        {
            return _store.IterateStagedTransactionIds();
        }

        /// <inheritdoc cref="IStore"/>
        public IEnumerable<TxId> IterateTransactionIds()
        {
            return _store.IterateTransactionIds();
        }

        /// <inheritdoc cref="IStore"/>
        public Transaction<T> GetTransaction<T>(TxId txid)
            where T : IAction, new()
        {
            return _store.GetTransaction<T>(txid);
        }

        /// <inheritdoc cref="IStore"/>
        public bool DeleteTransaction(TxId txid)
        {
            if (!Select<TransactionModel, string>(TxDbName, "tx_id", txid.ToHex()).Any())
            {
                return false;
            }

            Delete(TxDbName, "tx_id", txid.ToHex());
            Delete(UpdatedAddressRefDbName, "tx_id", txid.ToByteArray());

            _store.DeleteTransaction(txid);
            return true;
        }

        /// <inheritdoc cref="IStore"/>
        public IEnumerable<HashDigest<SHA256>> IterateBlockHashes()
        {
            return _store.IterateBlockHashes();
        }

        /// <inheritdoc cref="IStore"/>
        public Block<T> GetBlock<T>(HashDigest<SHA256> blockHash)
            where T : IAction, new()
        {
            return _store.GetBlock<T>(blockHash);
        }

        public IEnumerable<HashDigest<SHA256>> GetBlockHashes(
            bool desc,
            int offset,
            int? limit,
            Address? miner)
        {
            using QueryFactory db = OpenDB();
            if (!(miner is null))
            {
                if (limit != null)
                {
                    var query = db.Query(BlockDbName).Where("miner", miner.ToString())
                        .Offset(offset)
                        .Limit((int)limit)
                        .Select("hash");
                    query = desc ? query.OrderByDesc("index") : query.OrderBy("index");
                    return query.OrderBy("index")
                        .Get<string>()
                        .Select(hashString => new HashDigest<SHA256>(
                            ByteUtil.ParseHex(hashString)));
                }
                else
                {
                    var query = db.Query(BlockDbName).Where("miner", miner.ToString())
                        .Offset(offset)
                        .Select("hash");
                    query = desc ? query.OrderByDesc("index") : query.OrderBy("index");
                    return query.OrderBy("index")
                        .Get<string>()
                        .Select(hashString => new HashDigest<SHA256>(
                            ByteUtil.ParseHex(hashString)));
                }
            }
            else
            {
                if (limit != null)
                {
                    var query = desc ? db.Query(BlockDbName).OrderByDesc("index") :
                        db.Query(BlockDbName).OrderBy("index");
                    query = query.Offset(offset).Select("hash");
                    return query.OrderBy("index")
                        .Get<string>()
                        .Select(hashString => new HashDigest<SHA256>(
                            ByteUtil.ParseHex(hashString)));
                }
                else
                {
                    var query = desc ? db.Query(BlockDbName).OrderByDesc("index") :
                        db.Query(BlockDbName).OrderBy("index");
                    query = query.Offset(offset).Limit((int)limit).Select("hash");
                    return query.OrderBy("index")
                        .Get<string>()
                        .Select(hashString => new HashDigest<SHA256>(
                            ByteUtil.ParseHex(hashString)));
                }
            }
        }

        public IEnumerable<HashDigest<SHA256>> GetBlockHashesWithTx(
            bool desc,
            int offset,
            int? limit,
            Address? miner)
        {
            using QueryFactory db = OpenDB();
            if (!(miner is null))
            {
                if (limit != null)
                {
                    var query = db.Query(BlockDbName).Where("miner", miner.ToString())
                        .WhereNot("tx_hash", string.Empty)
                        .Offset(offset)
                        .Limit((int)limit)
                        .Select("hash");
                    query = desc ? query.OrderByDesc("index") : query.OrderBy("index");
                    return query.OrderBy("index")
                        .Get<string>()
                        .Select(hashString => new HashDigest<SHA256>(
                            ByteUtil.ParseHex(hashString)));
                }
                else
                {
                    var query = db.Query(BlockDbName).Where("miner", miner.ToString())
                        .WhereNot("tx_hash", string.Empty)
                        .Offset(offset)
                        .Select("hash");
                    query = desc ? query.OrderByDesc("index") : query.OrderBy("index");
                    return query.OrderBy("index")
                        .Get<string>()
                        .Select(hashString => new HashDigest<SHA256>(
                            ByteUtil.ParseHex(hashString)));
                }
            }
            else
            {
                if (limit != null)
                {
                    var query = desc ? db.Query(BlockDbName).OrderByDesc("index") :
                        db.Query(BlockDbName).OrderBy("index");
                    query = query
                        .WhereNot("tx_hash", string.Empty)
                        .Offset(offset)
                        .Select("hash");
                    return query.OrderBy("index")
                        .Get<string>()
                        .Select(hashString => new HashDigest<SHA256>(
                            ByteUtil.ParseHex(hashString)));
                }
                else
                {
                    var query = desc ? db.Query(BlockDbName).OrderByDesc("index") :
                        db.Query(BlockDbName).OrderBy("index");
                    query = query
                        .WhereNot("tx_hash", string.Empty)
                        .Offset(offset)
                        .Limit((int)limit)
                        .Select("hash");
                    return query.OrderBy("index")
                        .Get<string>()
                        .Select(hashString => new HashDigest<SHA256>(
                            ByteUtil.ParseHex(hashString)));
                }
            }
        }

        public void PutTransaction<T>(Transaction<T> tx)
            where T : IAction, new()
        {
            _store.PutTransaction(tx);
            StoreUpdatedAddressReferences(tx);
            StoreSignerReferences(tx.Id, tx.Nonce, tx.Signer);
            string txFilePath = Path.GetTempFileName();
            using StreamWriter txBulkFile = new StreamWriter(txFilePath);
            try
            {
                txBulkFile.WriteLine(
                    $"{tx.Id.ToString()}," +
                    $"{tx.Nonce}," +
                    $"{tx.Signer.ToString()}," +
                    $"{ByteUtil.Hex(tx.Signature)}," +
                    $"{tx.Timestamp.ToString()}," +
                    $"{ByteUtil.Hex(tx.PublicKey.Format(true))}," +
                    $"{tx.GenesisHash?.ToString()}," +
                    $"{tx.BytesLength}");
                txBulkFile.Flush();
                txBulkFile.Close();
                BulkInsert(TxDbName, txFilePath);
            }
            catch (Exception e)
            {
                Log.Debug(e.Message);
            }
            finally
            {
                txBulkFile.Dispose();
            }
        }

        public void SetBlockPerceivedTime(
            HashDigest<SHA256> blockHash,
            DateTimeOffset perceivedTime)
        {
            _store.SetBlockPerceivedTime(blockHash, perceivedTime);
        }

        public void StoreTxReferences(TxId txId, HashDigest<SHA256> blockHash, long txNonce)
        {
            string txRefFilePath = Path.GetTempFileName();
            using StreamWriter txRefBulkFile = new StreamWriter(txRefFilePath);
            try
            {
                txRefBulkFile.WriteLine(
                    $"{txId.ToString()}," +
                    $"{blockHash.ToString()}," +
                    $"{txNonce}");
                txRefBulkFile.Flush();
                txRefBulkFile.Close();
                BulkInsert(TxRefDbName, txRefFilePath);
            }
            catch (Exception e)
            {
                Log.Debug(e.Message);
            }
            finally
            {
                txRefBulkFile.Dispose();
            }
        }

        public IEnumerable<ValueTuple<TxId, HashDigest<SHA256>>> IterateTxReferences(
            TxId? txId = null,
            bool desc = false,
            int offset = 0,
            int limit = int.MaxValue)
        {
            using QueryFactory db = OpenDB();
            Query query = db.Query(TxRefDbName).Select(new[] { "tx_id", "block_hash" });
            if (!(txId is null))
            {
                query = query.Where("tx_id", txId?.ToString());
            }

            query = desc ? query.OrderByDesc("tx_nonce") : query.OrderBy("tx_nonce");
            query = query.Offset(offset).Limit(limit);
            return db.GetDictionary(query).Select(dict => new ValueTuple<TxId, HashDigest<SHA256>>(
                new TxId(ByteUtil.ParseHex(dict["tx_id"].ToString())),
                new HashDigest<SHA256>(ByteUtil.ParseHex(dict["block_hash"].ToString()))));
        }

        public void StoreSignerReferences(TxId txId, long txNonce, Address signer)
        {
            string signerRefFilePath = Path.GetTempFileName();
            using StreamWriter signerRefBulkFile = new StreamWriter(signerRefFilePath);
            try
            {
                signerRefBulkFile.WriteLine(
                    $"{signer.ToString()}," +
                    $"{txId.ToHex()}," +
                    $"{txNonce}");
                signerRefBulkFile.Flush();
                signerRefBulkFile.Close();
                BulkInsert(SignerRefDbName, signerRefFilePath);
            }
            catch (Exception e)
            {
                Log.Debug(e.Message);
            }
            finally
            {
                signerRefBulkFile.Dispose();
            }
        }

        public IEnumerable<TxId> IterateSignerReferences(
            Address signer,
            bool desc,
            int offset = 0,
            int limit = int.MaxValue)
        {
            using QueryFactory db = OpenDB();
            var query = db.Query(SignerRefDbName).Where("signer", signer.ToString())
                .Offset(offset)
                .Limit(limit)
                .Select("tx_id");
            query = desc ? query.OrderByDesc("tx_nonce") : query.OrderBy("tx_nonce");
            return query.OrderBy("tx_nonce")
                .Get<string>()
                .Select(txString => new TxId(ByteUtil.ParseHex(txString)));
        }

        public void StoreUpdatedAddressReferences<T>(Transaction<T> tx)
            where T : IAction, new()
        {
            string updatedAddressRefFilePath = Path.GetTempFileName();
            using StreamWriter updatedAddressRefBulkFile = new StreamWriter(
                updatedAddressRefFilePath);
            try
            {
                foreach (Address address in tx.UpdatedAddresses)
                {
                    updatedAddressRefBulkFile.WriteLine(
                        $"{address.ToString()}," +
                        $"{tx.Id.ToString()}," +
                        $"{tx.Nonce}");
                }

                updatedAddressRefBulkFile.Flush();
                updatedAddressRefBulkFile.Close();
                BulkInsert(UpdatedAddressRefDbName, updatedAddressRefFilePath);
            }
            catch (Exception e)
            {
                Log.Debug(e.Message);
            }
            finally
            {
                updatedAddressRefBulkFile.Dispose();
            }
        }

        public IEnumerable<TxId> IterateUpdatedAddressReferences(
            Address updatedAddress,
            bool desc,
            int offset = 0,
            int limit = int.MaxValue)
        {
            using QueryFactory db = OpenDB();
            var query = db.Query(UpdatedAddressRefDbName)
                .Where("updated_address", updatedAddress.ToByteArray())
                .Offset(offset)
                .Limit(limit)
                .Select("tx_id");
            query = desc ? query.OrderByDesc("tx_nonce") : query.OrderBy("tx_nonce");
            return query.OrderBy("tx_nonce")
                .Get<string>()
                .Select(txString => new TxId(ByteUtil.ParseHex(txString)));
        }

        private QueryFactory OpenDB() =>
            new QueryFactory(new MySqlConnection(_connectionString), _compiler);

        private IList<TModel> Select<TModel, TInput>(
            string tableName,
            string column,
            TInput id)
        {
            using QueryFactory db = OpenDB();
            try
            {
                var rows = db.Query(tableName).Where(column, id).Get<TModel>();
                return rows.ToList();
            }
            catch (MySqlException e)
            {
                Log.Debug(e.ErrorCode.ToString());
                throw;
            }
        }

        private void Insert<T>(
            string tableName,
            IReadOnlyDictionary<string, object> data,
            string key,
            T value)
        {
            using QueryFactory db = OpenDB();
            try
            {
                db.Query(tableName).Insert(data);
            }
            catch (MySqlException e)
            {
                if (e.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
                {
                    if (key != null && value != null)
                    {
                        Update(tableName, data, key, value);
                    }

                    Log.Debug($"Update DuplicateKeyEntry in {tableName}");
                }
                else
                {
                    throw;
                }
            }
        }

        private void BulkInsert(
            string tableName,
            string filePath)
        {
            using MySqlConnection connection = new MySqlConnection(_connectionString);
            try
            {
                MySqlBulkLoader loader = new MySqlBulkLoader(connection)
                {
                    TableName = tableName,
                    FileName = filePath,
                    Timeout = 0,
                    LineTerminator = "\n",
                    FieldTerminator = ",",
                    Local = true,
                    ConflictOption = MySqlBulkLoaderConflictOption.Replace,
                };
                loader.Load();
            }
            catch (Exception e)
            {
                Log.Debug(e.Message);
            }
        }

        private void InsertMany(
            string tableName,
            string[] columns,
            IEnumerable<object[]> data)
        {
            using QueryFactory db = OpenDB();
            try
            {
                db.Query(tableName).Insert(columns, data);
            }
            catch (MySqlException e)
            {
                if (e.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
                {
                    Log.Debug("Ignore DuplicateKeyEntry");
                }
                else
                {
                    throw;
                }
            }
        }

        private void Update<T>(
            string tableName,
            IReadOnlyDictionary<string, object> data,
            string key,
            T value)
        {
            using QueryFactory db = OpenDB();
            try
            {
                db.Query(tableName).Where(key, value).Update(data);
            }
            catch (MySqlException e)
            {
                if (e.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
                {
                    Log.Debug($"Ignore DuplicateKeyEntry in {tableName}");
                }
                else
                {
                    throw;
                }
            }
        }

        private void Delete<T>(string tableName, string column, T id)
        {
            using QueryFactory db = OpenDB();
            try
            {
                db.Query(tableName).Where(column, id).Delete();
            }
            catch (MySqlException e)
            {
                Log.Debug(e.Message);
                throw;
            }
        }
    }
}
