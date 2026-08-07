using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatCMS.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CloudSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloudSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Profiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    JoinCode = table.Column<string>(type: "TEXT", nullable: false),
                    AutoApprove = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoUpdateLocal = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyOffline = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyUpdate = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyRecipients = table.Column<string>(type: "TEXT", nullable: true),
                    SyncSettings = table.Column<bool>(type: "INTEGER", nullable: false),
                    UseGlobalSmtp = table.Column<bool>(type: "INTEGER", nullable: false),
                    SyncUsers = table.Column<bool>(type: "INTEGER", nullable: false),
                    SyncPlugins = table.Column<bool>(type: "INTEGER", nullable: false),
                    SyncComponents = table.Column<bool>(type: "INTEGER", nullable: false),
                    SyncTemplates = table.Column<bool>(type: "INTEGER", nullable: false),
                    ActivateTemplateName = table.Column<string>(type: "TEXT", nullable: true),
                    OverwriteSettings = table.Column<bool>(type: "INTEGER", nullable: false),
                    OverwritePlugins = table.Column<bool>(type: "INTEGER", nullable: false),
                    OverwriteComponents = table.Column<bool>(type: "INTEGER", nullable: false),
                    OverwriteTemplates = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Profiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StoreComponents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Icon = table.Column<string>(type: "TEXT", nullable: false),
                    FieldsJson = table.Column<string>(type: "TEXT", nullable: false),
                    TemplateHtml = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoreComponents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StorePlugins",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Bundle = table.Column<byte[]>(type: "BLOB", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorePlugins", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StoreTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    AccentColor = table.Column<string>(type: "TEXT", nullable: false),
                    SecondaryColor = table.Column<string>(type: "TEXT", nullable: false),
                    HeadingFont = table.Column<string>(type: "TEXT", nullable: false),
                    BodyFont = table.Column<string>(type: "TEXT", nullable: false),
                    ButtonStyle = table.Column<string>(type: "TEXT", nullable: false),
                    HeadingColor = table.Column<string>(type: "TEXT", nullable: false),
                    TextColor = table.Column<string>(type: "TEXT", nullable: false),
                    BackgroundColor = table.Column<string>(type: "TEXT", nullable: false),
                    AltBackground = table.Column<string>(type: "TEXT", nullable: false),
                    ContainerWidth = table.Column<string>(type: "TEXT", nullable: false),
                    ButtonRadius = table.Column<string>(type: "TEXT", nullable: false),
                    HeaderBackground = table.Column<string>(type: "TEXT", nullable: false),
                    HeaderTextColor = table.Column<string>(type: "TEXT", nullable: false),
                    HeaderPadding = table.Column<string>(type: "TEXT", nullable: false),
                    CustomCss = table.Column<string>(type: "TEXT", nullable: false),
                    CustomJs = table.Column<string>(type: "TEXT", nullable: false),
                    LayoutHtml = table.Column<string>(type: "TEXT", nullable: false),
                    MenuMapJson = table.Column<string>(type: "TEXT", nullable: false),
                    ParametersJson = table.Column<string>(type: "TEXT", nullable: false),
                    ParamValuesJson = table.Column<string>(type: "TEXT", nullable: false),
                    SchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    PartsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoreTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    Email = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Instances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicId = table.Column<string>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: true),
                    AppliedRevision = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSyncError = table.Column<string>(type: "TEXT", nullable: true),
                    LastSyncUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    LastHeartbeatUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Version = table.Column<string>(type: "TEXT", nullable: true),
                    ProtocolVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    HostName = table.Column<string>(type: "TEXT", nullable: true),
                    ContainerId = table.Column<string>(type: "TEXT", nullable: true),
                    ImageRef = table.Column<string>(type: "TEXT", nullable: true),
                    Hosting = table.Column<int>(type: "INTEGER", nullable: false),
                    LocalContainerName = table.Column<string>(type: "TEXT", nullable: true),
                    LocalPort = table.Column<int>(type: "INTEGER", nullable: true),
                    PageCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PluginCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UserCount = table.Column<int>(type: "INTEGER", nullable: false),
                    OfflineNotified = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdateNotifiedVersion = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Instances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Instances_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ProfileComponents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Icon = table.Column<string>(type: "TEXT", nullable: false),
                    FieldsJson = table.Column<string>(type: "TEXT", nullable: false),
                    TemplateHtml = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileComponents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileComponents_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfilePlugins",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Bundle = table.Column<byte[]>(type: "BLOB", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfilePlugins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfilePlugins_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfileSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true),
                    IsSecret = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileSettings_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfileTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    AccentColor = table.Column<string>(type: "TEXT", nullable: false),
                    SecondaryColor = table.Column<string>(type: "TEXT", nullable: false),
                    HeadingFont = table.Column<string>(type: "TEXT", nullable: false),
                    BodyFont = table.Column<string>(type: "TEXT", nullable: false),
                    ButtonStyle = table.Column<string>(type: "TEXT", nullable: false),
                    HeadingColor = table.Column<string>(type: "TEXT", nullable: false),
                    TextColor = table.Column<string>(type: "TEXT", nullable: false),
                    BackgroundColor = table.Column<string>(type: "TEXT", nullable: false),
                    AltBackground = table.Column<string>(type: "TEXT", nullable: false),
                    ContainerWidth = table.Column<string>(type: "TEXT", nullable: false),
                    ButtonRadius = table.Column<string>(type: "TEXT", nullable: false),
                    HeaderBackground = table.Column<string>(type: "TEXT", nullable: false),
                    HeaderTextColor = table.Column<string>(type: "TEXT", nullable: false),
                    HeaderPadding = table.Column<string>(type: "TEXT", nullable: false),
                    CustomCss = table.Column<string>(type: "TEXT", nullable: false),
                    CustomJs = table.Column<string>(type: "TEXT", nullable: false),
                    LayoutHtml = table.Column<string>(type: "TEXT", nullable: false),
                    MenuMapJson = table.Column<string>(type: "TEXT", nullable: false),
                    ParametersJson = table.Column<string>(type: "TEXT", nullable: false),
                    ParamValuesJson = table.Column<string>(type: "TEXT", nullable: false),
                    SchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    PartsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileTemplates_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfileUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileUsers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileUsers_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfileStoreComponents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    StoreComponentId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileStoreComponents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileStoreComponents_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProfileStoreComponents_StoreComponents_StoreComponentId",
                        column: x => x.StoreComponentId,
                        principalTable: "StoreComponents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfileStorePlugins",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    StorePluginId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileStorePlugins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileStorePlugins_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProfileStorePlugins_StorePlugins_StorePluginId",
                        column: x => x.StorePluginId,
                        principalTable: "StorePlugins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfileStoreTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    StoreTemplateId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileStoreTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileStoreTemplates_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProfileStoreTemplates_StoreTemplates_StoreTemplateId",
                        column: x => x.StoreTemplateId,
                        principalTable: "StoreTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfileGlobalUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileGlobalUsers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileGlobalUsers_Profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProfileGlobalUsers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InstanceEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InstanceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Notified = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstanceEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstanceEvents_Instances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "Instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CloudSettings_Key",
                table: "CloudSettings",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstanceEvents_InstanceId_CreatedAt",
                table: "InstanceEvents",
                columns: new[] { "InstanceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Instances_ProfileId",
                table: "Instances",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Instances_PublicId",
                table: "Instances",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Instances_TokenHash",
                table: "Instances",
                column: "TokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_ProfileComponents_ProfileId_Type",
                table: "ProfileComponents",
                columns: new[] { "ProfileId", "Type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProfileGlobalUsers_ProfileId_UserId",
                table: "ProfileGlobalUsers",
                columns: new[] { "ProfileId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProfileGlobalUsers_UserId",
                table: "ProfileGlobalUsers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProfilePlugins_ProfileId_Key",
                table: "ProfilePlugins",
                columns: new[] { "ProfileId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProfileSettings_ProfileId_Key",
                table: "ProfileSettings",
                columns: new[] { "ProfileId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProfileStoreComponents_ProfileId_StoreComponentId",
                table: "ProfileStoreComponents",
                columns: new[] { "ProfileId", "StoreComponentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProfileStoreComponents_StoreComponentId",
                table: "ProfileStoreComponents",
                column: "StoreComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_ProfileStorePlugins_ProfileId_StorePluginId",
                table: "ProfileStorePlugins",
                columns: new[] { "ProfileId", "StorePluginId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProfileStorePlugins_StorePluginId",
                table: "ProfileStorePlugins",
                column: "StorePluginId");

            migrationBuilder.CreateIndex(
                name: "IX_ProfileStoreTemplates_ProfileId_StoreTemplateId",
                table: "ProfileStoreTemplates",
                columns: new[] { "ProfileId", "StoreTemplateId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProfileStoreTemplates_StoreTemplateId",
                table: "ProfileStoreTemplates",
                column: "StoreTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_ProfileTemplates_ProfileId_Name",
                table: "ProfileTemplates",
                columns: new[] { "ProfileId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProfileUsers_ProfileId_Username",
                table: "ProfileUsers",
                columns: new[] { "ProfileId", "Username" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_JoinCode",
                table: "Profiles",
                column: "JoinCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Profiles_Name",
                table: "Profiles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StoreComponents_Type",
                table: "StoreComponents",
                column: "Type",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StorePlugins_Key",
                table: "StorePlugins",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StoreTemplates_Name",
                table: "StoreTemplates",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CloudSettings");

            migrationBuilder.DropTable(
                name: "InstanceEvents");

            migrationBuilder.DropTable(
                name: "ProfileComponents");

            migrationBuilder.DropTable(
                name: "ProfileGlobalUsers");

            migrationBuilder.DropTable(
                name: "ProfilePlugins");

            migrationBuilder.DropTable(
                name: "ProfileSettings");

            migrationBuilder.DropTable(
                name: "ProfileStoreComponents");

            migrationBuilder.DropTable(
                name: "ProfileStorePlugins");

            migrationBuilder.DropTable(
                name: "ProfileStoreTemplates");

            migrationBuilder.DropTable(
                name: "ProfileTemplates");

            migrationBuilder.DropTable(
                name: "ProfileUsers");

            migrationBuilder.DropTable(
                name: "Instances");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "StoreComponents");

            migrationBuilder.DropTable(
                name: "StorePlugins");

            migrationBuilder.DropTable(
                name: "StoreTemplates");

            migrationBuilder.DropTable(
                name: "Profiles");
        }
    }
}
