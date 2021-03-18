namespace MySqlStore.Models
{
    public class BlockModel
    {
        public long Index { get; set; }

        public string Hash { get; set; }

        public string PreEvaluationHash { get; set; }

        public string StateRootHash { get; set; }

        public long Difficulty { get; set; }

        public long TotalDifficulty { get; set; }

        public string Nonce { get; set; }

        public string Miner { get; set; }

        public string PreviousHash { get; set; }

        public string Timestamp { get; set; }

        public string TxHash { get; set; }

        public int ProtocolVersion { get; set; }
    }
}