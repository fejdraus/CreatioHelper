namespace CreatioHelper.Application.Interfaces
{
    public interface IOutputWriter
    {
        void WriteLine(string message);
        void Clear();
    }
}