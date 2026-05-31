using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using API.Workers;
using API.Schema.ActionsContext;
using API.Schema.ActionsContext.Actions;

namespace API.Tests.Workers;

public class MoveFileOrFolderWorkerTests : IDisposable
{
    private readonly string _testRoot;
    private readonly Mock<IServiceScope> _mockScope;
    private readonly ActionsContext _context;

    public MoveFileOrFolderWorkerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"WorkerTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testRoot);

        var options = new DbContextOptionsBuilder<ActionsContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        // 2. Assign the real context to the class field
        _context = new ActionsContext(options);

        var serviceProvider = new Mock<IServiceProvider>();

        // 3. Return _context here instead of the old realContext variable
        serviceProvider.Setup(x => x.GetService(typeof(ActionsContext))).Returns(_context);

        _mockScope = new Mock<IServiceScope>();
        _mockScope.Setup(x => x.ServiceProvider).Returns(serviceProvider.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, true);
    }

    [Fact]
    public async Task DoWork_WhenSourceIsFile_MovesFileSuccessfully()
    {
        var sourceFile = Path.Combine(_testRoot, "old_file.txt");
        var destFile = Path.Combine(_testRoot, "new_file.txt");
        File.WriteAllText(sourceFile, "Unit Test Content");

        var worker = new MoveFileOrFolderWorker(destFile, sourceFile);

        await worker.DoWork(_mockScope.Object);

        Assert.False(File.Exists(sourceFile));
        Assert.True(File.Exists(destFile));
        Assert.Equal("Unit Test Content", File.ReadAllText(destFile));
    }

    [Fact]
    public async Task DoWork_WhenSourceIsDirectory_MovesDirectorySuccessfully()
    {
        var sourceDir = Path.Combine(_testRoot, "SourceFolder");
        var destDir = Path.Combine(_testRoot, "DestFolder");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "data.txt"), "hello");

        var worker = new MoveFileOrFolderWorker(destDir, sourceDir);

        await worker.DoWork(_mockScope.Object);

        Assert.False(Directory.Exists(sourceDir));
        Assert.True(Directory.Exists(destDir));
        Assert.True(File.Exists(Path.Combine(destDir, "data.txt")));
    }

    [Fact]
    public async Task DoWork_WhenTargetDirectoryDoesNotExist_CreatesDirectoryAndMovesFile()
    {
        string nestedFolder = Path.Combine(_testRoot, "deep", "path", "dne");
        string sourceFile = Path.Combine(_testRoot, "source.txt");
        string destFile = Path.Combine(nestedFolder, "target.txt");
        File.WriteAllText(sourceFile, "Persistence pays off");

        var worker = new MoveFileOrFolderWorker(destFile, sourceFile);

        await worker.DoWork(_mockScope.Object);

        Assert.True(Directory.Exists(nestedFolder));
        Assert.True(File.Exists(destFile));
        Assert.Equal("Persistence pays off", File.ReadAllText(destFile));
    }

    [Fact]
    public async Task DoWork_WhenDestinationExists_DoesNotMove()
    {
        var source = Path.Combine(_testRoot, "source.txt");
        var dest = Path.Combine(_testRoot, "dest.txt");
        File.WriteAllText(source, "source content");
        File.WriteAllText(dest, "already exists");

        var worker = new MoveFileOrFolderWorker(dest, source);

        await worker.DoWork(_mockScope.Object);

        Assert.True(File.Exists(source));
        Assert.Equal("already exists", File.ReadAllText(dest));
    }

    [Fact]
    public async Task DoWork_WhenMovingDirectoryIntoItself_CatchesExceptionAndAborts()
    {
        var sourceDir = Path.Combine(_testRoot, "ParentFolder");
        var destDir = Path.Combine(sourceDir, "ChildFolder");

        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "data.txt"), "hello");

        var worker = new MoveFileOrFolderWorker(destDir, sourceDir);
        await worker.DoWork(_mockScope.Object);

        Assert.True(Directory.Exists(sourceDir));
        Assert.True(File.Exists(Path.Combine(sourceDir, "data.txt")));
        Assert.Empty(_context.Actions);
    }

    [Fact]
    public async Task DoWork_WithUnicodeAndSpecialCharactersInPath_MovesSuccessfully()
    {
        string weirdFolderName = "Series 漫画 📁 (Vol 1)";
        string weirdFileName = "chapter_01_🚀_final (copy).cbz";

        var sourceFile = Path.Combine(_testRoot, weirdFileName);
        var destDir = Path.Combine(_testRoot, weirdFolderName);
        var destFile = Path.Combine(destDir, weirdFileName);

        File.WriteAllText(sourceFile, "binary data");

        var worker = new MoveFileOrFolderWorker(destFile, sourceFile);
        await worker.DoWork(_mockScope.Object);

        Assert.False(File.Exists(sourceFile));
        Assert.True(File.Exists(destFile));

        var actionRecord = _context.Actions.First() as DataMovedActionRecord;
        Assert.NotNull(actionRecord);
        Assert.Equal(sourceFile, actionRecord.From);
        Assert.Equal(destFile, actionRecord.To);
    }

    [Fact]
    public async Task DoWork_WhenChangingCaseOnly_HandlesSuccessfully()
    {
        var sourceFile = Path.Combine(_testRoot, "UPPERCASE.txt");
        var destFile = Path.Combine(_testRoot, "uppercase.txt");
        File.WriteAllText(sourceFile, "data");

        var worker = new MoveFileOrFolderWorker(destFile, sourceFile);
        await worker.DoWork(_mockScope.Object);

        Assert.True(File.Exists(destFile));

        Assert.Single(_context.Actions);
    }
}
