FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY truck-kun-tower.csproj ./
RUN dotnet restore
COPY Program.cs Duel.cs Maze.cs Data.cs Storage.cs WaveClock.cs ./
COPY wwwroot ./wwwroot
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_NOLOGO=1 \
    ConnectionStrings__Default="Data Source=/data/duel.db"
COPY --from=build /app ./
RUN mkdir -p /data && chown app:app /data
USER app
VOLUME ["/data"]
EXPOSE 8080
ENTRYPOINT ["dotnet", "truck-kun-tower.dll"]
