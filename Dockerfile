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
# the page must ask for a new address when one changes. Hash the assets it marks with @hash@
RUN cd wwwroot \
 && assets=$(grep -oE '[[:alnum:]_./-]+[?]v=@hash@' index.html | cut -d '?' -f 1) \
 && test -n "$assets" \
 && hashes=$(md5sum $assets) \
 && stamp=$(printf '%s\n' "$hashes" | md5sum | cut -c1-12) \
 && sed -i "s/@hash@/$stamp/g" index.html \
 && grep -q "?v=$stamp" index.html
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "truck-kun-tower.dll"]
