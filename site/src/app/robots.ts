import type { MetadataRoute } from "next";

export const dynamic = "force-static";

export default function robots(): MetadataRoute.Robots {
  return {
    rules: { userAgent: "*", allow: "/ytray/" },
    sitemap: "https://yaklang.io/ytray/sitemap.xml",
    host: "https://yaklang.io",
  };
}
