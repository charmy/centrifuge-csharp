namespace Centrifuge
{
    public class HistoryOptions
    {
        public int Limit { get; }
        public StreamPosition Since { get; }
        public bool Reverse { get; }

        public HistoryOptions(int limit, StreamPosition since, bool reverse)
        {
            Limit = limit;
            Since = since;
            Reverse = reverse;
        }

        public override string ToString()
        {
            return "HistoryOptions: " + Limit + ", " + Since + ", reverse " + Reverse;
        }
    }
}