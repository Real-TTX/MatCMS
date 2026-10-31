// Matmon live: the service status and the sensor catalogue of matmon.eu as blocks. Code in live.csx, the partner list in partners.csx, the changelog in changelog.csx, the bundled catalogue in snapshot.csx.
#load "snapshot.csx"
#load "live.csx"
#load "partners.csx"
#load "changelog.csx"

MmLive.Configure(Plugin);
MmPartners.Configure(Plugin);
MmChangelog.Configure(Plugin);
