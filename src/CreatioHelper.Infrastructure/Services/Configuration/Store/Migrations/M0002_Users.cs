using FluentMigrator;

namespace CreatioHelper.Infrastructure.Services.Configuration.Store.Migrations;

[Migration(2, "User accounts")]
public class M0002_Users : Migration
{
    public override void Up()
    {
        Create.Table("config_users")
            .WithColumn("username").AsString(256).PrimaryKey()
            .WithColumn("password_hash").AsString(int.MaxValue).NotNullable()
            .WithColumn("role").AsString(32).NotNullable().WithDefaultValue("user")
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("updated_at").AsDateTime().Nullable();
    }

    public override void Down()
    {
        Delete.Table("config_users");
    }
}
