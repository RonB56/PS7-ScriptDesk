using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Tests;

public sealed class ApiAuthenticationIntentPolicyTests
{
    [Theory]
    [InlineData(ApiSecurityMode.LocalTestNoAuthentication, false)]
    [InlineData(ApiSecurityMode.ApiKey, true)]
    public void PersistedDefaultIntentTracksGlobalModeAcrossRepeatedTransitions(ApiSecurityMode mode, bool expected)
    {
        var config = Configuration();
        config.Security.Mode = ApiSecurityMode.LocalTestNoAuthentication;
        var reopened = System.Text.Json.JsonSerializer.Deserialize<ApiPublishConfiguration>(
            System.Text.Json.JsonSerializer.Serialize(config))!;

        reopened.Security.Mode = ApiSecurityMode.ApiKey;
        ApiAuthenticationIntentPolicy.Apply(reopened);
        reopened.Security.Mode = ApiSecurityMode.LocalTestNoAuthentication;
        ApiAuthenticationIntentPolicy.Apply(reopened);
        reopened.Security.Mode = mode;
        ApiAuthenticationIntentPolicy.Apply(reopened);

        Assert.Equal(expected, Assert.Single(reopened.Endpoints).RequiresAuthentication);
        Assert.True(reopened.Endpoints[0].AuthenticationRequirementIsDefaulted);
    }

    [Fact]
    public void ExplicitPublicAndProtectedPreferencesPersistAcrossTransitions()
    {
        var config = Configuration();
        config.Endpoints[0].AuthenticationRequirementIsDefaulted = false;
        config.Endpoints[0].RequiresAuthentication = false;
        var protectedEndpoint = ApiEndpointConfiguration.CreateRest("Get-Protected", ApiHttpMethod.Get, "/protected");
        protectedEndpoint.AuthenticationRequirementIsDefaulted = false;
        protectedEndpoint.RequiresAuthentication = true;
        config.Endpoints.Add(protectedEndpoint);

        var reopened = System.Text.Json.JsonSerializer.Deserialize<ApiPublishConfiguration>(
            System.Text.Json.JsonSerializer.Serialize(config))!;
        reopened.Security.Mode = ApiSecurityMode.ApiKey;
        ApiAuthenticationIntentPolicy.Apply(reopened);
        reopened.Security.Mode = ApiSecurityMode.LocalTestNoAuthentication;
        ApiAuthenticationIntentPolicy.Apply(reopened);
        reopened.Security.Mode = ApiSecurityMode.ApiKey;
        ApiAuthenticationIntentPolicy.Apply(reopened);

        Assert.False(reopened.Endpoints[0].RequiresAuthentication);
        Assert.True(reopened.Endpoints[1].RequiresAuthentication);
        Assert.All(reopened.Endpoints, endpoint => Assert.False(endpoint.AuthenticationRequirementIsDefaulted));
    }

    [Fact]
    public void LegacyUnknownFalseIsProtectedAndReported()
    {
        var config = Configuration();
        config.Endpoints[0].AuthenticationRequirementIsDefaulted = null;
        config.Endpoints[0].RequiresAuthentication = false;
        config.Security.Mode = ApiSecurityMode.ApiKey;

        var result = ApiAuthenticationIntentPolicy.Apply(config);

        Assert.True(config.Endpoints[0].RequiresAuthentication);
        Assert.Contains(config.Endpoints[0].EndpointId, result.LegacyEndpointsProtected);
        Assert.Null(config.Endpoints[0].AuthenticationRequirementIsDefaulted);
    }

    [Fact]
    public void InvalidMetadataDoesNotSkipAuthenticationPolicyOnLoadedConfiguration()
    {
        var config = Configuration();
        config.Endpoints[0].AuthenticationRequirementIsDefaulted = null;
        config.Endpoints[0].RequiresAuthentication = false;
        config.Security.Mode = ApiSecurityMode.ApiKey;
        var metadata = new ApiMetadataResult(false, null,
            [new ApiSyntaxError("ParserError", "invalid script", null)], [], []);

        var result = new ApiPublishConfigurationReconciler().Reconcile(config, metadata);

        Assert.True(config.Endpoints[0].RequiresAuthentication);
        Assert.Contains(result.Findings, finding => finding.Kind == ApiPublishReconciliationFindingKind.LegacyAuthenticationProtected);
        Assert.False(result.MetadataIsValid);
    }

    [Fact]
    public void ProgrammaticRowSynchronizationDoesNotClearPersistedIntent()
    {
        var endpoint = ApiEndpointConfiguration.CreateRest("Get-Default", ApiHttpMethod.Get, "/default");
        endpoint.AuthenticationRequirementIsDefaulted = true;
        var row = PS7ScriptDesk.Shell.Dialogs.RestApiEndpointRow.FromConfiguration(endpoint, ApiTransport.Rest);

        row.SetProgrammaticAuthentication(true);

        Assert.True(row.ToConfiguration().AuthenticationRequirementIsDefaulted);
    }

    [Fact]
    public void ProgrammaticAuthenticationUpdatesNotifyOnlyWhenEffectiveValueChanges()
    {
        var endpoint = ApiEndpointConfiguration.CreateRest("Get-Default", ApiHttpMethod.Get, "/default");
        endpoint.AuthenticationRequirementIsDefaulted = true;
        var row = PS7ScriptDesk.Shell.Dialogs.RestApiEndpointRow.FromConfiguration(endpoint, ApiTransport.Rest);
        var notifications = 0;
        row.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(row.RequiresAuthentication)) notifications++;
        };

        row.SetProgrammaticAuthentication(true);
        row.SetProgrammaticAuthentication(false);

        Assert.Equal(1, notifications);
        Assert.False(row.RequiresAuthentication);
        Assert.True(row.AuthenticationRequirementIsDefaulted);
    }

    [Fact]
    public void UnchangedAuthenticationCellCommitPreservesInheritedIntent()
    {
        var row = CreateInheritedRow();

        row.BeginAuthenticationEdit();
        row.EndAuthenticationEdit(commit: true);

        Assert.True(row.AuthenticationRequirementIsDefaulted);
    }

    [Fact]
    public void CanceledAuthenticationCellEditRestoresValueAndIntent()
    {
        var row = CreateInheritedRow();

        row.BeginAuthenticationEdit();
        row.RequiresAuthentication = true;
        row.EndAuthenticationEdit(commit: false);

        Assert.False(row.RequiresAuthentication);
        Assert.True(row.AuthenticationRequirementIsDefaulted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeliberateAuthenticationCellCommitEstablishesExplicitIntent(bool preference)
    {
        var row = CreateInheritedRow(!preference);

        row.BeginAuthenticationEdit();
        row.RequiresAuthentication = preference;
        row.EndAuthenticationEdit(commit: true);

        Assert.Equal(preference, row.RequiresAuthentication);
        Assert.False(row.AuthenticationRequirementIsDefaulted);
    }

    private static PS7ScriptDesk.Shell.Dialogs.RestApiEndpointRow CreateInheritedRow(bool requiresAuthentication = false)
    {
        var endpoint = ApiEndpointConfiguration.CreateRest("Get-Default", ApiHttpMethod.Get, "/default");
        endpoint.RequiresAuthentication = requiresAuthentication;
        endpoint.AuthenticationRequirementIsDefaulted = true;
        return PS7ScriptDesk.Shell.Dialogs.RestApiEndpointRow.FromConfiguration(endpoint, ApiTransport.Rest);
    }

    private static ApiPublishConfiguration Configuration()
    {
        var endpoint = ApiEndpointConfiguration.CreateRest("Get-Default", ApiHttpMethod.Get, "/default");
        endpoint.RequiresAuthentication = false;
        endpoint.AuthenticationRequirementIsDefaulted = true;
        return new ApiPublishConfiguration { Endpoints = [endpoint] };
    }
}
