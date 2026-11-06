# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

COPY ZscalerRootCA.crt /usr/local/share/ca-certificates/

RUN update-ca-certificates

WORKDIR /src

COPY . .

RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0

WORKDIR /app

COPY --from=build /app/publish .

EXPOSE 8080

ENTRYPOINT ["dotnet", "WebApi.dll"]