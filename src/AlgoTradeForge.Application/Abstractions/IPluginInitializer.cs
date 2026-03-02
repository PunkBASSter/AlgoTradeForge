using Microsoft.Extensions.DependencyInjection;

namespace AlgoTradeForge.Application.Abstractions;

public interface IPluginInitializer
{
    void ConfigureServices(IServiceCollection services);
}
