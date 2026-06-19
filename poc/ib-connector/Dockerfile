FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# IbPoc.csproj references ../ibapi/IBApi.csproj, so both trees must be present.
COPY ibapi/ ./ibapi/
COPY src/ ./src/
RUN dotnet publish src/IbPoc.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "IbPoc.dll"]
