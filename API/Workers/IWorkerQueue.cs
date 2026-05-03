namespace API.Workers;

public interface IWorkerQueue
{
    void AddWorker(BaseWorker worker);
    void AddWorkers(IEnumerable<BaseWorker> workers);
    BaseWorker[] GetKnownWorkers();
    BaseWorker[] GetRunningWorkers();
    void StopWorker(BaseWorker worker);
}
