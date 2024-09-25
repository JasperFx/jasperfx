namespace Widgets1;

public interface Column
{
    void Display();
}

public class BasicColumn : Column
{
    #region Column Members

    public void Display()
    {
    }

    #endregion
}

public class DateColumn : Column
{
    public DateColumn(string HeaderName, int Width, string FieldName)
    {
        this.HeaderName = HeaderName;
        this.Width = Width;
        this.FieldName = FieldName;
    }

    /// <summary>
    ///     Just a shell to test whether the correct constructor is being called
    /// </summary>
    /// <param name="HeaderName"></param>
    public DateColumn(string HeaderName)
    {
    }

    public string HeaderName { get; }

    public int Width { get; }

    public string FieldName { get; }

    #region Column Members

    public void Display()
    {
    }

    #endregion
}

public class NumberColumn : Column
{
    #region Column Members

    public void Display()
    {
    }

    #endregion
}