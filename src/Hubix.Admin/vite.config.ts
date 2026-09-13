import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Builds straight into the ASP.NET host's wwwroot, so one `dotnet run` serves everything.
// Fixed filenames rather than hashed ones: the Razor page references them, and
// asp-append-version already busts the cache on content change.
export default defineConfig({
  plugins: [react()],
  base: '/admin-assets/',
  build: {
    outDir: '../Hubix.Web/wwwroot/admin-assets',
    emptyOutDir: true,
    rollupOptions: {
      // drawings.js is for the public apartment page, which references it by name. It shares
      // previewImage and pdf.js with the admin rather than carrying a second copy of either.
      input: { index: 'index.html', drawings: 'src/drawings.ts' },
      output: {
        entryFileNames: '[name].js',
        chunkFileNames: '[name].js',
        // pdf.js's worker arrives as .mjs, which the host's static files may not serve as
        // JavaScript. It is a module either way, so it ships as .js under its own name.
        assetFileNames: (asset) =>
          asset.names.some((n) => n.startsWith('pdf.worker')) ? 'pdf.worker.js' : 'index.[ext]',
      },
    },
  },
  server: {
    port: 5200,
    // `npm run dev` talks to the running ASP.NET host for data and auth.
    proxy: {
      '/api': { target: 'http://localhost:5001', changeOrigin: true },
    },
  },
});
