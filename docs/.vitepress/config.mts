import { defineConfig } from 'vitepress'
import { withMermaid } from 'vitepress-plugin-mermaid'

export default withMermaid(
  defineConfig({
    title: 'JasperFx',
    description: 'The foundational .NET library behind the Critter Stack',
    head: [
      ['link', { rel: 'icon', href: '/jasperfx-logo.png' }]
    ],

    themeConfig: {
      logo: '/jasperfx-logo.png',

      nav: [
        { text: 'Guide', link: '/guide/' },
        { text: 'Code Generation', link: '/codegen/' },
        { text: 'Command Line', link: '/cli/' },
        { text: 'Configuration', link: '/configuration/critter-stack-defaults' },
        {
          text: 'Ecosystem',
          items: [
            { text: 'Marten', link: 'https://martendb.io' },
            { text: 'Wolverine', link: 'https://wolverinefx.io' },
            { text: 'Weasel', link: 'https://weasel.jasperfx.net' },
            { text: 'GitHub', link: 'https://github.com/JasperFx/jasperfx' }
          ]
        }
      ],

      sidebar: [
        {
          text: 'Getting Started',
          collapsed: false,
          items: [
            { text: 'Introduction', link: '/guide/' },
            { text: 'Installation', link: '/guide/installation' },
            { text: 'Quick Start', link: '/guide/quickstart' }
          ]
        },
        {
          text: 'Code Generation',
          collapsed: false,
          items: [
            { text: 'Overview & Architecture', link: '/codegen/' },
            { text: 'Frames', link: '/codegen/frames' },
            { text: 'Variables', link: '/codegen/variables' },
            { text: 'MethodCall', link: '/codegen/method-call' },
            { text: 'Generated Types & Methods', link: '/codegen/generated-types' },
            { text: 'Built-in Frames', link: '/codegen/built-in-frames' },
            { text: 'CLI: codegen Command', link: '/codegen/cli' }
          ]
        },
        {
          text: 'Command Line',
          collapsed: false,
          items: [
            { text: 'Setup & Integration', link: '/cli/' },
            { text: 'Writing Commands', link: '/cli/writing-commands' },
            { text: 'Arguments & Flags', link: '/cli/arguments-flags' },
            { text: 'Environment Checks', link: '/cli/environment-checks' },
            { text: 'Describe Command', link: '/cli/describe' }
          ]
        },
        {
          text: 'Configuration',
          collapsed: true,
          items: [
            { text: 'CritterStackDefaults', link: '/configuration/critter-stack-defaults' },
            { text: 'JasperFxOptions', link: '/configuration/jasperfx-options' }
          ]
        },
        {
          text: 'Extension Methods',
          collapsed: true,
          items: [
            { text: 'Overview', link: '/extensions/' },
            { text: 'String Extensions', link: '/extensions/string-extensions' },
            { text: 'Enumerable Extensions', link: '/extensions/enumerable-extensions' },
            { text: 'Reflection Extensions', link: '/extensions/reflection-extensions' }
          ]
        },
        {
          text: 'Release Notes',
          collapsed: true,
          items: [
            { text: '1.26', link: '/release_notes/1.26' },
            { text: '1.25', link: '/release_notes/1.25' }
          ]
        }
      ],

      socialLinks: [
        { icon: 'github', link: 'https://github.com/JasperFx/jasperfx' }
      ],

      editLink: {
        pattern: 'https://github.com/JasperFx/jasperfx/edit/main/docs/:path'
      },

      footer: {
        message: 'Released under the MIT License.',
        copyright: 'Copyright JasperFx Software'
      },

      search: {
        provider: 'local'
      }
    },

    mermaid: {},
    mermaidPlugin: {
      class: 'mermaid'
    }
  })
)
