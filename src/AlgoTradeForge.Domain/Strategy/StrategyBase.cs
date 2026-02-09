using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AlgoTradeForge.Domain.Strategy;

public abstract class StrategyBase<TParams> where TParams : StrategyParamsBase
{
    //TODO add strategy run hash to identify unique runs and results
}
