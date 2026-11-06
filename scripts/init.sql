IF DB_ID('BatchDb') IS NULL
    CREATE DATABASE BatchDb;
GO

USE BatchDb;
GO

IF NOT EXISTS (
    SELECT * FROM sys.server_principals
    WHERE name = 'batchuser'
)
BEGIN
    CREATE LOGIN batchuser
    WITH PASSWORD = 'BatchDb2026!@';
END
GO

IF NOT EXISTS (
    SELECT * FROM sys.database_principals
    WHERE name = 'batchuser'
)
BEGIN
    CREATE USER batchuser
    FOR LOGIN batchuser;
END
GO

ALTER ROLE db_owner
ADD MEMBER batchuser;
GO