namespace AlgoTradeForge.Domain.Validation.Stages;

public interface IValidationStage
{
    int StageNumber { get; }
    string StageName { get; }
    StageResult Execute(ValidationContext context, CancellationToken ct = default);
}
