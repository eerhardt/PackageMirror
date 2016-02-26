# PackageMirror

A simple WebAPI service that listens for [MyGet Web Hook](http://docs.myget.org/docs/reference/webhooks) "Package Added" requests 
to {rootUrl}/packageadded. When invoked, PackageMirror will inspect the package that was added, and if it
matches the appropriate filters for the feed, PackageMirror will push the package to a destination/mirror feed.

To use, set the following required app settings:
- DestinationUrl - the feed URL for the destination feed
- DestinationApiKey - the ApiKey that authorizes PackageMirror to push a package to the destination feed
- At least one "filter" app settings

## Filter App Settings ##

A filter app setting takes the following form:

The app setting 'key' is a URL of the upstream feed. Any POST request for an upstream feed that isn't registered
with a filter app setting will be ignored. This ensures only packages for registered feeds are mirrored.

The app setting 'value' applies a filter to the packages that should be mirrored. A blank value means all
packages should be mirrored. Multiple filters can be specified by separating them with a vertical bar `|` character.
Packages that match any filter will be mirrored.

A filter has two parts separated by a dash `-`. 

The first part specifies what the filter applies to. Currently supported are:
- ID - the package identifier
- V - the package version

The second part of the filter is a normal .NET Regex which will be applied to the package.

Examples:

`<add key="https://dotnet.myget.org/F/dotnet-corert/" value=""/>`

- all packages from dotnet-corert will be mirrored

`<add key="https://dotnet.myget.org/F/dotnet-core/" value="V-\d+.\d+.\d+-rc2-.*|ID-xunit"/>`

- any package with a Version of the form X.Y.Z-rc2-* or any package with "xunit" in its Identifier will be mirrored.
