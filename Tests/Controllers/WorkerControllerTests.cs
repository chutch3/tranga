using API.Controllers;
using API.Controllers.DTOs;
using API.Workers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace API.Tests.Controllers;

public class WorkerControllerTests
{
    /// <summary>
    /// Minimal concrete BaseWorker that completes immediately and exposes a settable state.
    /// </summary>
    private sealed class FakeWorker(string key, WorkerExecutionState initialState = WorkerExecutionState.Running)
        : BaseWorker(key)
    {
        protected internal override WorkerExecutionState State { get; protected set; } = initialState;
        protected override Task<BaseWorker[]> DoWorkInternal() => Task.FromResult(Array.Empty<BaseWorker>());
    }

    private static WorkerController CreateController(IWorkerQueue workerQueue)
    {
        var controller = new WorkerController(workerQueue);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    [Fact]
    public void GetWorkers_ReturnsAllKnownWorkers()
    {
        var w1 = new FakeWorker("worker-1");
        var w2 = new FakeWorker("worker-2");
        var queue = new Mock<IWorkerQueue>();
        queue.Setup(q => q.GetKnownWorkers()).Returns([w1, w2]);

        var result = CreateController(queue.Object).GetWorkers();

        var ok = Assert.IsType<Ok<List<Worker>>>(result);
        Assert.Equal(2, ok.Value!.Count);
        Assert.Contains(ok.Value, w => w.Key == "worker-1");
        Assert.Contains(ok.Value, w => w.Key == "worker-2");
    }

    [Fact]
    public void GetWorkers_WhenEmpty_ReturnsEmptyList()
    {
        var queue = new Mock<IWorkerQueue>();
        queue.Setup(q => q.GetKnownWorkers()).Returns([]);

        var result = CreateController(queue.Object).GetWorkers();

        var ok = Assert.IsType<Ok<List<Worker>>>(result);
        Assert.Empty(ok.Value!);
    }

    [Fact]
    public void GetWorkersInState_FiltersToRequestedState()
    {
        var running = new FakeWorker("running-1", WorkerExecutionState.Running);
        var waiting = new FakeWorker("waiting-1", WorkerExecutionState.Waiting);
        var queue = new Mock<IWorkerQueue>();
        queue.Setup(q => q.GetKnownWorkers()).Returns([running, waiting]);

        var result = CreateController(queue.Object).GetWorkersInState(WorkerExecutionState.Running);

        var ok = Assert.IsType<Ok<List<Worker>>>(result);
        Assert.Single(ok.Value!);
        Assert.Equal("running-1", ok.Value![0].Key);
    }

    [Fact]
    public void GetWorkersInState_WhenNoneMatch_ReturnsEmptyList()
    {
        var running = new FakeWorker("running-1", WorkerExecutionState.Running);
        var queue = new Mock<IWorkerQueue>();
        queue.Setup(q => q.GetKnownWorkers()).Returns([running]);

        var result = CreateController(queue.Object).GetWorkersInState(WorkerExecutionState.Waiting);

        var ok = Assert.IsType<Ok<List<Worker>>>(result);
        Assert.Empty(ok.Value!);
    }

    [Fact]
    public void GetWorker_KnownId_ReturnsWorker()
    {
        var worker = new FakeWorker("worker-abc");
        var queue = new Mock<IWorkerQueue>();
        queue.Setup(q => q.GetKnownWorkers()).Returns([worker]);

        var result = CreateController(queue.Object).GetWorker("worker-abc");

        var ok = Assert.IsType<Ok<Worker>>(result.Result);
        Assert.Equal("worker-abc", ok.Value!.Key);
    }

    [Fact]
    public void GetWorker_UnknownId_ReturnsNotFound()
    {
        var queue = new Mock<IWorkerQueue>();
        queue.Setup(q => q.GetKnownWorkers()).Returns([]);

        var result = CreateController(queue.Object).GetWorker("does-not-exist");

        Assert.IsType<NotFound<string>>(result.Result);
    }

    [Fact]
    public void StopWorker_KnownRunningWorker_StopsAndReturnsOk()
    {
        var worker = new FakeWorker("worker-stop", WorkerExecutionState.Running);
        var queue = new Mock<IWorkerQueue>();
        queue.Setup(q => q.GetRunningWorkers()).Returns([worker]);

        var result = CreateController(queue.Object).StopWorker("worker-stop");

        Assert.IsType<Ok>(result.Result);
        queue.Verify(q => q.StopWorker(worker), Times.Once);
    }

    [Fact]
    public void StopWorker_UnknownId_ReturnsNotFound()
    {
        var queue = new Mock<IWorkerQueue>();
        queue.Setup(q => q.GetRunningWorkers()).Returns([]);

        var result = CreateController(queue.Object).StopWorker("ghost-worker");

        Assert.IsType<NotFound<string>>(result.Result);
    }

    [Fact]
    public void StopWorker_WorkerAlreadyCompleted_Returns412()
    {
        var worker = new FakeWorker("worker-done", WorkerExecutionState.Completed);
        var queue = new Mock<IWorkerQueue>();
        queue.Setup(q => q.GetRunningWorkers()).Returns([worker]);

        var result = CreateController(queue.Object).StopWorker("worker-done");

        var statusCode = Assert.IsType<StatusCodeHttpResult>(result.Result);
        Assert.Equal(412, statusCode.StatusCode);
        queue.Verify(q => q.StopWorker(It.IsAny<BaseWorker>()), Times.Never);
    }
}
