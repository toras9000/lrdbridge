namespace lrdbridgeTests;

[TestClass]
public class DisposeActionTests
{
    [TestMethod]
    public void Dispose()
    {
        var exec = false;
        using (var reserve = new DisposeAction(() => exec = true))
        {
            exec.Should().BeFalse();
        }
        exec.Should().BeTrue();
    }
}
