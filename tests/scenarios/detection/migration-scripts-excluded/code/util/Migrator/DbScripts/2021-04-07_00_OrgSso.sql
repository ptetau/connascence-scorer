IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'UseSso' AND Object_ID = Object_ID(N'[dbo].[Organization]'))
BEGIN
    ALTER TABLE [dbo].[Organization] ADD [UseSso] BIT NULL
END
GO
UPDATE [dbo].[Organization] SET [UseSso] = 0
GO
EXEC sp_refreshview N'[dbo].[OrganizationView]'
GO
IF OBJECT_ID('[dbo].[Organization_ReadAbilities]') IS NOT NULL
BEGIN
    DROP PROCEDURE [dbo].[Organization_ReadAbilities]
END
GO
CREATE PROCEDURE [dbo].[Organization_ReadAbilities]
AS
BEGIN
    SET NOCOUNT ON
    SELECT [Id], [UseEvents], [Use2fa], [UseSso], [Enabled] FROM [dbo].[Organization]
END
GO
