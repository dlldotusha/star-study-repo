FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY StarStudy.slnx ./
COPY StarStudy/StarStudy.csproj StarStudy/
COPY StarStudy.Admin/StarStudy.Admin.csproj StarStudy.Admin/
COPY StarStudy.Client/StarStudy.Client.csproj StarStudy.Client/
RUN dotnet restore StarStudy.slnx

COPY . .
RUN dotnet publish StarStudy/StarStudy.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "StarStudy.dll"]
