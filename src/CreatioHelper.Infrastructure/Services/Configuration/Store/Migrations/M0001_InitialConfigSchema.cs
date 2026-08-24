using FluentMigrator;

namespace CreatioHelper.Infrastructure.Services.Configuration.Store.Migrations;

[Migration(1, "Initial configuration schema")]
public class M0001_InitialConfigSchema : Migration
{
    public override void Up()
    {
        Create.Table("config_devices")
            .WithColumn("device_id").AsString(64).PrimaryKey()
            .WithColumn("name").AsString().Nullable()
            .WithColumn("admitted_at").AsDateTime().Nullable()
            .WithColumn("state_version").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("paused").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("introducer").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("data").AsString(int.MaxValue).NotNullable().WithDefaultValue("{}");

        Create.Table("config_folders")
            .WithColumn("folder_id").AsString(256).PrimaryKey()
            .WithColumn("label").AsString().Nullable()
            .WithColumn("path").AsString().Nullable()
            .WithColumn("type").AsString(32).Nullable()
            .WithColumn("paused").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("data").AsString(int.MaxValue).NotNullable().WithDefaultValue("{}");

        Create.Table("config_folder_devices")
            .WithColumn("folder_id").AsString(256).NotNullable().PrimaryKey("pk_config_folder_devices")
            .WithColumn("device_id").AsString(64).NotNullable().PrimaryKey("pk_config_folder_devices")
            .WithColumn("introduced_by").AsString(64).Nullable()
            .WithColumn("encryption_password").AsString().Nullable();

        Create.Table("config_ignored_devices")
            .WithColumn("device_id").AsString(64).PrimaryKey()
            .WithColumn("name").AsString().Nullable()
            .WithColumn("deleted_at").AsDateTime().NotNullable()
            .WithColumn("address").AsString().Nullable()
            .WithColumn("state_version").AsInt64().NotNullable().WithDefaultValue(0);

        Create.Table("config_singletons")
            .WithColumn("key").AsString(64).PrimaryKey()
            .WithColumn("data").AsString(int.MaxValue).NotNullable().WithDefaultValue("{}");
    }

    public override void Down()
    {
        Delete.Table("config_singletons");
        Delete.Table("config_ignored_devices");
        Delete.Table("config_folder_devices");
        Delete.Table("config_folders");
        Delete.Table("config_devices");
    }
}
