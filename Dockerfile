FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY truck-kun-tower.csproj ./
RUN dotnet restore
# Every source folder is copied: a new file must not depend on someone remembering the Dockerfile
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_NOLOGO=1
COPY --from=build /app ./
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "truck-kun-tower.dll"]
