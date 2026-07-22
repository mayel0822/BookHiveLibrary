# Stage 1: Build Tailwind CSS
FROM node:20-slim AS css-build
WORKDIR /app
COPY package.json package-lock.json* ./
RUN npm install
COPY wwwroot/css/input.css ./wwwroot/css/input.css
COPY tailwind.config.js* ./
# Copy views so Tailwind can scan for classes
COPY Views/ ./Views/
COPY Pages/ ./Pages/
RUN npm run css:build

# Stage 2: Build .NET app
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY *.csproj ./
RUN dotnet restore
COPY . .
# Bring in compiled CSS from stage 1
COPY --from=css-build /app/wwwroot/css/output.css ./wwwroot/css/output.css
# Skip the npm Tailwind build step since we already did it
RUN dotnet publish -c Release -o /app/publish \
    /p:SkipTailwindBuild=true

# Stage 3: Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "BookHiveLibrary.dll"]
