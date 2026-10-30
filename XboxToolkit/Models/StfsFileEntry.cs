namespace XboxToolkit.Models
{
    public class StfsFileEntry
    {
        public string Path { get; internal set; }

        public bool IsDirectory { get; internal set; }

        public int Size { get; internal set; }

        internal int StartBlock;

        internal int BlockCount;

        internal bool Consecutive;

        internal byte[] RawEntry;

        public StfsFileEntry()
        {
            Path = string.Empty;
            RawEntry = new byte[0x40];
        }
    }
}
