namespace CreatioHelper.Core
{
    public class IisSiteInfo
    {
        public long Id { get; set; } = -1;
        public string Name { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;
        public string PoolName { get; set; } = string.Empty;

        public override string ToString() => Name;
    }
}