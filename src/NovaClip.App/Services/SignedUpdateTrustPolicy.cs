namespace NovaClip.App;

internal static class SignedUpdateTrustPolicy
{
    public const string KeyId = "novaclip-beta7-2026";
    // The corresponding private key is never stored in the repository. Release signing must use a protected CI secret.
    public const string PublicKeyPem = """-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA1k8Ll2CwX8sWPASf9dx6
S8VDahjgO+wnXRQqX3EUa2h001fs8DnFMMmhQBSBPFDOYxYsRUnBFWmla7/hYyCV
gIpYcFaGQedokoXXyeW8bB1Ua86RnvFghF6D2HHB5euNhhhw+YHPtERP7zEy21BZ
X/TC/bSNdfWPHVR6Q+QIIdznb65odc28+bRNvwTVzmQrIBzFrYh3mlncyFmoztXq
mAGcyBXUcS09Mx9jEfGEKMTNTIkNWKUSWmHl9xRCjpt+fHTfyOWbk1MB2jGwhI3+
7dWNENVOTxI7E6Lv+olL4D+ggJfzpoiTDRmLYh+xeVUlDIeoMke75B/nJKCzimyr
6wIDAQAB
-----END PUBLIC KEY-----""";
}
