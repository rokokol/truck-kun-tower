FROM mcr.microsoft.com/dotnet/sdk:8.0@sha256:1b955f179b02df4f0eb5fadedaa898cbe44d620a64aab52eec72c8e91d1355b5 AS build
WORKDIR /src
COPY truck-kun-tower.csproj ./
RUN dotnet restore
# Every source folder is copied: a new file must not depend on someone remembering the Dockerfile
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0@sha256:dccacae9e0e103b5d3ba5fbe442dd8d3316b990cd8f2ec9be0a0ec9db0c0e520
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_NOLOGO=1
COPY --from=build /app ./
# Cloudflare keeps a script or a stylesheet for four hours whatever the origin says, so
# the page must ask for a new address when one changes. Hash the assets it marks with @hash@
RUN cd wwwroot \
 && assets=$(grep -oE '[[:alnum:]_./-]+[?]v=@hash@' index.html | cut -d '?' -f 1) \
 && test -n "$assets" \
 && hashes=$(md5sum $assets vendor/ddlc/*.woff2) \
 && stamp=$(printf '%s\n' "$hashes" | md5sum | cut -c1-12) \
 && sed -i "s/@hash@/$stamp/g" index.html \
 && sed -i -E "s@([A-Za-z0-9_-]+\\.woff2)([\"')])@\\1?v=$stamp\\2@g" duel.css vendor/ddlc/ddlc-ui.css \
 && grep -q "?v=$stamp" index.html
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "truck-kun-tower.dll"]
