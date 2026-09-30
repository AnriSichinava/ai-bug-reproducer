FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY BugReproducer.slnx ./
COPY src/BugReproducer.Api/BugReproducer.Api.csproj src/BugReproducer.Api/
COPY src/BugReproducer.Agent/BugReproducer.Agent.csproj src/BugReproducer.Agent/
RUN dotnet restore src/BugReproducer.Api/BugReproducer.Api.csproj
COPY src/BugReproducer.Api/ src/BugReproducer.Api/
COPY src/BugReproducer.Agent/ src/BugReproducer.Agent/
RUN dotnet publish src/BugReproducer.Api/BugReproducer.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "BugReproducer.Api.dll"]
