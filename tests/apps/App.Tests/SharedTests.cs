using Xunit;

public class SharedTests
{
    [Fact]
    public void ReadsSharedLibrary()
    {
        Assert.Equal("hello", App.Greeting.Message);
    }
}
