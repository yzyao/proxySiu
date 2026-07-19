FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ProxySiu.Api.csproj ./
RUN dotnet restore ProxySiu.Api.csproj

COPY . ./
RUN dotnet publish ProxySiu.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

ENV Urls=http://0.0.0.0:5080
EXPOSE 5080

ENTRYPOINT ["dotnet", "ProxySiu.Api.dll"]
