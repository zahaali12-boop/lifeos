namespace Quicker.Persistence;

/// <summary>
/// The unit of work of the current request or job. The host opens it (middleware or job runner) and modules
/// resolve it through this scoped accessor so every write in a request shares one transaction.
/// </summary>
public interface IUnitOfWorkAccessor
{
    IUnitOfWork Current { get; }

    bool HasCurrent { get; }

    void Set(IUnitOfWork unitOfWork);
}

public sealed class UnitOfWorkAccessor : IUnitOfWorkAccessor
{
    private IUnitOfWork? _current;

    public IUnitOfWork Current => _current ?? throw new InvalidOperationException("No unit of work is open for this scope.");

    public bool HasCurrent => _current is not null;

    public void Set(IUnitOfWork unitOfWork) => _current = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
}
