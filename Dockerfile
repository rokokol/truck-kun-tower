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
# Cloudflare keeps a script or a stylesheet for four hours whatever the origin says, so
# the page must ask for a new address when one changes. The stamp is the hash of those
# four files, not a number anyone has to remember to bump
RUN cd wwwroot \
 && stamp=$(cat duel.css game.js lobby.js sounds.js | md5sum | cut -c1-12) \
 && sed -i "s/@hash@/$stamp/g" index.html \
 && grep -q "?v=$stamp" index.html
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "truck-kun-tower.dll"]
