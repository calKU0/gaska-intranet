using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gaska.Intranet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCameraList : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// Written by hand to be repeatable: an early build of this feature created the same tables under another
        /// migration id (with an extra "Name" column) – existing tables are kept with their rows, the column is dropped.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'[intranet].[Cameras]') IS NULL
                CREATE TABLE [intranet].[Cameras] (
                    [Id] nvarchar(64) NOT NULL,
                    [Host] nvarchar(255) NOT NULL,
                    [HttpPort] int NOT NULL,
                    [RtspPort] int NOT NULL,
                    [Channel] int NOT NULL,
                    [HasSubStream] bit NOT NULL,
                    [Location] nvarchar(32) NULL,
                    [AlsoIn] nvarchar(256) NOT NULL,
                    [DeviceName] nvarchar(128) NULL,
                    [OsdName] nvarchar(128) NULL,
                    [CreatedAtUtc] datetime2 NOT NULL,
                    [UpdatedAtUtc] datetime2 NOT NULL,
                    [UpdatedBy] nvarchar(128) NULL,
                    CONSTRAINT [PK_Cameras] PRIMARY KEY ([Id])
                );
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH(N'intranet.Cameras', N'Name') IS NOT NULL
                    ALTER TABLE [intranet].[Cameras] DROP COLUMN [Name];
                """);

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'[intranet].[CameraViewOrders]') IS NULL
                CREATE TABLE [intranet].[CameraViewOrders] (
                    [UserSid] nvarchar(184) NOT NULL,
                    [Location] nvarchar(32) NOT NULL,
                    [CameraIds] nvarchar(max) NOT NULL,
                    [UpdatedAtUtc] datetime2 NOT NULL,
                    CONSTRAINT [PK_CameraViewOrders] PRIMARY KEY ([UserSid], [Location])
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Cameras",
                schema: "intranet");

            migrationBuilder.DropTable(
                name: "CameraViewOrders",
                schema: "intranet");
        }
    }
}
