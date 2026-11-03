import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mmc_pages import *
import mmc_pages as P

P.DT['string'] = ('String', None); P.DT['boolean'] = ('Boolean', None)
P.CONF['string'] = 'Shall be a string.'; P.CONF['boolean'] = 'Shall be true or false.'
def dt2(acr_, label=None):
    n, h = P.DT[acr_]
    return html.escape(label or n) if h is None else a(h, label or n)
P.dt = dt2
dt = dt2

# ------------------------------------------------------------------ the AIMs
MCA = Spec(
    acr='MMC-MCA', name='Multimodal Conversation Avatar', slug='multimodal-conversation-avatar', schema='MultimodalConversationAvatar.json',
    function=('The Multimodal Conversation Avatar (MMC‑MCA) AIM conducts a conversation between a human and a machine represented by a Portable Avatar. The human’s turn is spoken (Speech Object) or typed (Text Object); '
              'the machine’s answer is given as a Portable Avatar that speaks it, with lip-synchronised Face Descriptors. The conversation is anonymous: the human has no identity, no ID Reconciliation takes place, '
              'and no Personal Status is used.'),
    steps=['A human provides a turn of the conversation as:\n<ol type="a">\n \t<li>Speech, or</li>\n \t<li>Text.</li>\n</ol>',
           'The User Agent provides the Avatar Model that represents the machine and the Machine ID.',
           'The machine provides the Portable Avatar that utters its response.'],
    inputs=[('Input Speech', 'OSD-BSO', 'Speech of the human’s turn, if spoken. Optional.'), ('Input Text', 'OSD-BTO', 'Text of the human’s turn, if typed. Optional.'),
            ('Avatar Model', 'OSD-B3O', 'The 3D model of the avatar that represents the machine.'), ('Machine ID', 'string', 'Identifier of the machine. Optional.')],
    outputs=[('Portable Avatar', 'PAF-PAV', 'The avatar that utters the machine’s response, with its Machine Speech and lip-synchronised Face Descriptors.')],
    operation=('The Multimodal Conversation Avatar (MMC‑MCA) AIM receives the human’s turn as a Speech Object, which Automatic Speech Recognition converts to Text, or as Text. Entity Dialogue Processing produces the machine’s '
               'response as Text, using the Summary of the conversation, which is internal to it. Text-to-Speech produces the Machine Speech, Generative Face Description the lip-synchronised Machine Face Descriptors, and '
               'Portable Avatar Multiplexing combines Speech, Text, Face Descriptors, Avatar Model and Machine ID into the Portable Avatar. Acquisition and delivery are the User Agent’s, not SubAIMs.'),
    subs=[('MMC-ASR', 'Recognises as Text an input Speech.', [dt('OSD-BSO', 'Speech Object')], ['Recognised ' + dt('OSD-BTO', 'Text')]),
          ('MMC-EDP', 'Receives the text of the human’s turn and produces the machine’s response Text, using the Summary of the conversation, which it keeps and updates.',
           [dt('OSD-BTO', 'Text') + ' of the turn'], ['Machine ' + dt('OSD-BTO', 'Text')]),
          ('MMC-TTS', 'Converts the machine’s Text into Machine Speech.', ['Machine ' + dt('OSD-BTO', 'Text')], ['Machine ' + dt('OSD-BSO', 'Speech')]),
          ('PAF-GFD', 'Produces the lip-synchronised Machine Face Descriptors from the machine’s Text and Speech.', ['Machine ' + dt('OSD-BTO', 'Text'), 'Machine ' + dt('OSD-BSO', 'Speech')], ['Machine ' + dt('PAF-FDO', 'Face Descriptors')]),
          ('PAF-PMX', 'Combines Speech, Text, Face Descriptors, Avatar Model and Machine ID into the Portable Avatar.',
           ['Machine ' + dt('OSD-BSO', 'Speech'), 'Machine ' + dt('OSD-BTO', 'Text'), 'Machine ' + dt('PAF-FDO', 'Face Descriptors'), dt('OSD-B3O', 'Avatar Model'), dt('string', 'Machine ID')], [dt('PAF-PAV', 'Portable Avatar')])],
    fig1=(['Input Speech', 'Input Text', 'Avatar Model', 'Machine ID'], ['Portable Avatar']),
    fig2=dict(w=1000, h=420,
              boxes=[('ASR', 190, 40, 130, 80, ['Automatic', 'Speech', 'Recognition']), ('EDP', 390, 110, 130, 100, ['Entity', 'Dialogue', 'Processing']), ('TTS', 580, 40, 110, 70, ['Text-to-', 'Speech']),
                     ('GFD', 580, 170, 110, 80, ['Generative', 'Face', 'Description']), ('PMX', 780, 120, 130, 130, ['Portable', 'Avatar', 'Multiplexing'])],
              arrows=[([(20, 80), (190, 80)], ['Input Speech'], 105, 66), ([(320, 80), (355, 80), (355, 140), (390, 140)], ['Recognised', 'Text'], 355, 54),
                      ([(20, 190), (390, 190)], ['Input Text'], 105, 176),
                      ([(520, 150), (550, 150), (550, 75), (580, 75)], ['Machine', 'Text'], 470, 84), ([(550, 150), (550, 210), (580, 210)], []),
                      ([(550, 210), (550, 280), (815, 280), (815, 250)], ['Machine Text'], 680, 266),
                      ([(690, 75), (735, 75), (735, 145), (780, 145)], ['Machine Speech'], 735, 60), ([(635, 110), (635, 170)], ['Machine', 'Speech'], 665, 135),
                      ([(690, 210), (780, 210)], ['Face', 'Descriptors'], 735, 186),
                      ([(20, 330), (845, 330), (845, 250)], ['Avatar Model'], 105, 316), ([(20, 375), (875, 375), (875, 250)], ['Machine ID'], 105, 361),
                      ([(910, 185), (990, 185)], ['Portable', 'Avatar'], 950, 162)]))

MTS = Spec(
    acr='MMC-MTS', name='Multimodal Translation Service', slug='multimodal-translation-service', schema='MultimodalTranslationService.json',
    function=('The Multimodal Translation Service (MMC‑MTS) AIM translates what a human says or types into the language named by a Language Selector and presents the translation through a Portable Avatar that speaks it with '
              'lip-synchronised Face Descriptors in the target language. The translated text is also provided. The service is not affective: no Personal Status is supplied, and the human is anonymous.'),
    steps=['A human provides:\n<ol type="a">\n \t<li>Speech, or text typed or supplied as a Text Object, to be translated.</li>\n \t<li>A Language Selector naming the target language.</li>\n</ol>',
           'The User Agent provides the Avatar Model that represents the machine and the Machine ID.',
           'The machine provides the Translated Text and the Portable Avatar that speaks it.'],
    inputs=[('Input Speech', 'OSD-BSO', 'Speech of the human, if spoken. Optional.'), ('Target Language', 'OSD-SEL', 'The language into which the input is translated.'),
            ('Text to Translate', 'OSD-BTO', 'Text typed by the human instead of speaking. Optional.'),
            ('Avatar Model', 'OSD-B3O', 'The 3D model of the avatar that represents the machine.'), ('Machine ID', 'string', 'Identifier of the machine. Optional.')],
    outputs=[('Translated Text', 'OSD-BTO', 'The translation as Text.'), ('Portable Avatar', 'PAF-PAV', 'The avatar that speaks the translation, with lip-synchronised Face Descriptors in the target language.')],
    operation=('The Multimodal Translation Service (MMC‑MTS) AIM receives the human’s speech, which Automatic Speech Recognition converts to Text, or text. Text-to-Text Translation translates the Text into the language named by the '
               'Language Selector. Response and Scene Rendering speaks the translation and produces the lip-synchronised Machine Face Descriptors in the target language. The translated Text is also an output. '
               'Acquisition and delivery are the User Agent’s, not SubAIMs.'),
    subs=[('MMC-ASR', 'Recognises as Text an input Speech.', [dt('OSD-BSO', 'Speech Object')], ['Recognised ' + dt('OSD-BTO', 'Text')]),
          ('MMC-TTT', 'Receives a Text and a Language Selector and produces its translation into the selected language.', [dt('OSD-BTO', 'Text') + ' (recognised or typed)', dt('OSD-SEL', 'Language Selector')], ['Translated ' + dt('OSD-BTO', 'Text')]),
          ('PAF-RSR', 'Receives the translated Text, the Avatar Model and the Machine ID and produces the Portable Avatar that speaks the Text with lip-synchronised Machine Face Descriptors.',
           ['Translated ' + dt('OSD-BTO', 'Text'), dt('OSD-B3O', 'Avatar Model'), dt('string', 'Machine ID')], [dt('PAF-PAV', 'Portable Avatar')])],
    fig1=(['Input Speech', 'Target Language', 'Text to Translate', 'Avatar Model', 'Machine ID'], ['Translated Text', 'Portable Avatar']),
    fig2=dict(w=1000, h=415,
              boxes=[('ASR', 190, 55, 140, 90, ['Automatic', 'Speech', 'Recognition']), ('TTT', 440, 130, 150, 120, ['Text-to-Text', 'Translation']), ('RSR', 730, 130, 160, 120, ['Response and', 'Scene Rendering'])],
              arrows=[([(20, 100), (190, 100)], ['Input Speech'], 105, 86), ([(330, 100), (390, 100), (390, 155), (440, 155)], ['Recognised', 'Text'], 385, 70),
                      ([(20, 195), (440, 195)], ['Text to Translate'], 130, 181), ([(20, 232), (440, 232)], ['Target Language'], 130, 218),
                      ([(590, 190), (730, 190)], ['Translated Text'], 660, 176), ([(660, 190), (660, 372), (990, 372)], ['Translated Text'], 830, 358),
                      ([(20, 290), (810, 290), (810, 250)], ['Avatar Model'], 120, 276), ([(20, 330), (860, 330), (860, 250)], ['Machine ID'], 120, 316),
                      ([(890, 190), (990, 190)], ['Portable', 'Avatar'], 945, 167)]))

AMC = Spec(
    acr='MMC-AMC', name='Affective Multimodal Conversation', slug='affective-multimodal-conversation', schema='AffectiveMultimodalConversation.json',
    function=('The Affective Multimodal Conversation (MMC‑AMC) AIM conducts a conversation in which the machine is aware both of what the human says and of how the human feels. It recognises the speech, understands the text, '
              'extracts the Personal Status from speech and, if provided, from the face, combines it with the Personal Status of the text, produces the machine’s response and the machine’s own Personal Status, and presents them as a '
              'Portable Avatar.'),
    steps=['A human provides:\n<ol type="a">\n \t<li>Speech, or text typed instead of speaking.</li>\n \t<li>Optionally, an image of the face.</li>\n</ol>',
           'The User Agent provides the Avatar Model that represents the machine and the Machine ID.',
           'The machine provides the Portable Avatar that utters its response with its own Personal Status.'],
    inputs=[('Input Speech', 'OSD-BSO', 'Speech of the human’s turn, if spoken.'), ('Input Face', 'OSD-BVO', 'Image of the human’s face, from which the Personal Status of the face is extracted. Optional.'),
            ('Input Text', 'OSD-BTO', 'Text typed by the human instead of speaking. Optional.'), ('Avatar Model', 'OSD-B3O', 'The 3D model of the avatar that represents the machine.'),
            ('Machine ID', 'string', 'Identifier of the machine. Optional.')],
    outputs=[('Portable Avatar', 'PAF-PAV', 'The avatar that utters the machine’s response with its Personal Status.')],
    operation=('The Affective Multimodal Conversation (MMC‑AMC) AIM receives the human’s speech, which Automatic Speech Recognition converts to Text. Natural Language Understanding produces the Text Descriptors, the refined Text and the '
               'Text Personal Status. Personal Status Extraction reads the Speech Personal Status from the Speech Object, and the Face Personal Status from the face if provided, and multiplexes them with the Text Personal Status into the Personal '
               'Status of the human. Entity Dialogue Processing produces the machine’s response Text and Personal Status, aware both of the meaning and of the feeling; the Summary of the conversation is internal to it. Response and Scene Rendering renders the Portable Avatar. '
               'Acquisition and delivery are the User Agent’s, not SubAIMs.'),
    subs=[('MMC-ASR', 'Recognises as Text an input Speech.', [dt('OSD-BSO', 'Speech Object')], ['Recognised ' + dt('OSD-BTO', 'Text')]),
          ('MMC-NLU', 'Understands the recognised Text: produces the refined Text, its Text Descriptors and the Text Personal Status.', ['Recognised ' + dt('OSD-BTO', 'Text')], ['Refined ' + dt('OSD-BTO', 'Text'), dt('MMC-TPS', 'Text Personal Status')]),
          ('MMC-PSE', 'Extracts the Personal Status of the speech and of the face and multiplexes them with the Text Personal Status into the Personal Status of the human.',
           [dt('OSD-BSO', 'Speech Object'), dt('OSD-BVO', 'Face'), dt('MMC-TPS', 'Text Personal Status')], [dt('MMC-EPS', 'Personal Status')]),
          ('MMC-EDP', 'Receives the refined Text and the Personal Status of the human and produces the machine’s response Text and its Personal Status; it keeps the Summary of the conversation internally.',
           ['Refined ' + dt('OSD-BTO', 'Text'), dt('MMC-EPS', 'Personal Status')], ['Machine ' + dt('OSD-BTO', 'Text'), 'Machine ' + dt('MMC-EPS', 'Personal Status')]),
          ('PAF-RSR', 'Receives the machine’s Text and Personal Status, the Avatar Model and the Machine ID and produces the Portable Avatar that speaks the Text with lip-synchronised Machine Face Descriptors.',
           ['Machine ' + dt('OSD-BTO', 'Text'), 'Machine ' + dt('MMC-EPS', 'Personal Status'), dt('OSD-B3O', 'Avatar Model'), dt('string', 'Machine ID')], [dt('PAF-PAV', 'Portable Avatar')])],
    fig1=(['Input Speech', 'Input Face', 'Input Text', 'Avatar Model', 'Machine ID'], ['Portable Avatar']),
    fig2=dict(w=1240, h=400,
              boxes=[('ASR', 170, 40, 130, 80, ['Automatic', 'Speech', 'Recognition']), ('NLU', 360, 40, 130, 80, ['Natural', 'Language', 'Understanding']), ('PSE', 550, 150, 130, 100, ['Personal', 'Status', 'Extraction']),
                     ('EDP', 760, 150, 140, 130, ['Entity', 'Dialogue', 'Processing']), ('RSR', 990, 150, 150, 130, ['Response and', 'Scene Rendering'])],
              arrows=[([(20, 80), (170, 80)], ['Input Speech'], 95, 66), ([(300, 80), (360, 80)], ['Text'], 330, 66), ([(425, 120), (425, 170), (550, 170)], ['Text', 'Personal Status'], 480, 140),
                      ([(20, 215), (550, 215)], ['Input Face'], 90, 201), ([(235, 40), (235, 25), (615, 25), (615, 150)], ['Speech'], 637, 62),
                      ([(490, 100), (700, 100), (700, 175), (760, 175)], ['Refined Text'], 640, 86), ([(680, 205), (760, 205)], ['Personal', 'Status'], 720, 187),
                      ([(20, 150), (325, 150), (325, 100), (360, 100)], ['Input Text'], 95, 136), ([(900, 180), (990, 180)], ['Machine Text'], 945, 166), ([(900, 225), (990, 225)], ['Machine', 'Personal Status'], 945, 207),
                      ([(20, 320), (1065, 320), (1065, 280)], ['Avatar Model'], 120, 306), ([(20, 360), (1110, 360), (1110, 280)], ['Machine ID'], 120, 346),
                      ([(1140, 215), (1230, 215)], ['Portable', 'Avatar'], 1185, 192)]))

ACR = Spec(
    acr='MMC-ACR', name='Access Control Registration', slug='access-control-registration', schema='AccessControlRegistration.json',
    function=('The Access Control Registration (MMC‑ACR) AIM enrols a User for access control by face and speech. From the captured Face Object and Speech Object it produces the Face Descriptors and the Speech Descriptors of the User '
              'and records them, under the User Name, in the gallery of enrolled Users that Multimodal Access Control (MMC‑MAC) reads. A Portable Avatar confirms the enrolment aloud.'),
    steps=['The human provides an image of the face and a speech sample, and types the User Name.',
           'The User Agent provides the Response to be spoken, the Personal Status of the avatar, the Avatar Model that represents the machine and the Machine ID.',
           'The machine records the Face Descriptors and the Speech Descriptors of the User under the User Name and provides the Portable Avatar that speaks the Response.'],
    inputs=[('Face Object', 'OSD-BVO', 'Image of the face of the User to be enrolled.'), ('Speech Object', 'OSD-BSO', 'Speech of the User to be enrolled.'),
            ('Personal Status', 'MMC-EPS', 'The Personal Status with which the avatar speaks.'), ('Response', 'OSD-BTO', 'The Text the avatar speaks to confirm the enrolment (Port 1).'),
            ('User Name', 'OSD-BTO', 'The name under which the User is enrolled, typed by the User (Port 2).'), ('Avatar Model', 'OSD-B3O', 'The 3D model of the avatar that represents the machine.'),
            ('Machine ID', 'string', 'Identifier of the machine. Optional.')],
    outputs=[('Portable Avatar', 'PAF-PAV', 'The avatar that speaks the confirmation, with lip-synchronised Face Descriptors.')],
    operation=('The Access Control Registration (MMC‑ACR) AIM receives the Face Object and the Speech Object of the User to be enrolled. Entity Face Description produces the Face Descriptors and Entity Speech Description produces the '
               'Speech Descriptors; both are recorded in the gallery of enrolled Users under the User Name. The gallery is Shared Storage, not a Port: Multimodal Access Control reads it to recognise the User. Response and Scene Rendering speaks '
               'the Response and produces the lip-synchronised Machine Face Descriptors. The User Name is typed by the User. Acquisition and delivery are the User Agent’s, not SubAIMs.'),
    subs=[('PAF-EFD', 'Produces the Face Descriptors of the User from the Face Object and records them in the gallery under the User Name.', [dt('OSD-BVO', 'Face Object'), dt('OSD-BTO', 'User Name')], ['Face Descriptors (gallery)']),
          ('MMC-ESD', 'Produces the Speech Descriptors of the User from the Speech Object and records them in the gallery under the User Name.', [dt('OSD-BSO', 'Speech Object'), dt('OSD-BTO', 'User Name')], ['Speech Descriptors (gallery)']),
          ('PAF-RSR', 'Receives the Response, the Personal Status, the Avatar Model and the Machine ID and produces the Portable Avatar that speaks the Response with lip-synchronised Machine Face Descriptors.',
           [dt('OSD-BTO', 'Response'), dt('MMC-EPS', 'Personal Status'), dt('OSD-B3O', 'Avatar Model'), dt('string', 'Machine ID')], [dt('PAF-PAV', 'Portable Avatar')])],
    fig1=(['Face Object', 'Speech Object', 'Personal Status', 'Response', 'User Name', 'Avatar Model', 'Machine ID'], ['Portable Avatar']),
    fig2=dict(w=1000, h=420,
              boxes=[('EFD', 300, 50, 150, 90, ['Entity Face', 'Description']), ('ESD', 300, 175, 150, 90, ['Entity Speech', 'Description']), ('RSR', 700, 190, 170, 130, ['Response and', 'Scene Rendering'])],
              arrows=[([(20, 85), (300, 85)], ['Face Object'], 150, 71), ([(20, 125), (300, 125)], ['User Name'], 150, 111), ([(180, 125), (180, 205), (300, 205)], None, 0, 0),
                      ([(20, 240), (300, 240)], ['Speech Object'], 150, 226), ([(20, 285), (700, 285)], ['Response (Port 1)'], 200, 271), ([(20, 325), (700, 325)], ['Personal Status'], 150, 311),
                      ([(20, 360), (775, 360), (775, 320)], ['Avatar Model'], 120, 346), ([(20, 395), (830, 395), (830, 320)], ['Machine ID'], 120, 381),
                      ([(870, 255), (990, 255)], ['Portable', 'Avatar'], 930, 232)]))

MAC = Spec(
    acr='MMC-MAC', name='Multimodal Access Control', slug='multimodal-access-control', schema='MultimodalAccessControl.json',
    function=('The Multimodal Access Control (MMC‑MAC) AIM recognises a User from the face and the speech and gives or refuses access. Face Identity Recognition and Speaker Identity Recognition read the gallery of enrolled Users, '
              'produced by Access Control Registration (MMC‑ACR); ID Reconciliation reconciles the two candidate identities and issues the verdict as a Response and its Personal Status. A Portable Avatar speaks the verdict; the verdict is also '
              'given as Text and as an identification flag.'),
    steps=['The human provides an image of the face and a speech sample.',
           'The User Agent provides, optionally, a Prompt to be spoken, the Avatar Model that represents the machine and the Machine ID.',
           'The machine provides the verdict as Text, as an identification flag and as the Portable Avatar that speaks it.'],
    inputs=[('Face Object', 'OSD-BVO', 'Image of the face of the User to be recognised.'), ('Speech Object', 'OSD-BSO', 'Speech of the User to be recognised.'),
            ('Prompt', 'OSD-BTO', 'Text the avatar speaks to prompt the User. Optional.'), ('Avatar Model', 'OSD-B3O', 'The 3D model of the avatar that represents the machine.'),
            ('Machine ID', 'string', 'Identifier of the machine. Optional.')],
    outputs=[('Response', 'OSD-BTO', 'The verdict in words.'), ('Identification', 'boolean', 'Whether the User was identified (true or false).'),
             ('Portable Avatar', 'PAF-PAV', 'The avatar that speaks the verdict, with lip-synchronised Face Descriptors.')],
    operation=('The Multimodal Access Control (MMC‑MAC) AIM receives the Face Object and the Speech Object. Face Identity Recognition and Speaker Identity Recognition each give the Instance Identifier of the User they recognise, reading the '
               'gallery of enrolled Users and the Scene Descriptors from Shared Storage, which are not Ports. ID Reconciliation reconciles the two Instance Identifiers and issues the Response and its Personal Status. Response and Scene Rendering '
               'speaks the Response and produces the lip-synchronised Machine Face Descriptors. The reconciled User ID stays inside the AIM. Acquisition and delivery are the User Agent’s, not SubAIMs.'),
    subs=[('PAF-FIR', 'Recognises the face against the gallery of enrolled Users and gives the Instance Identifier of the User.', [dt('OSD-BVO', 'Face Object')], [dt('OSD-IID', 'Instance Identifier') + ' (from face)']),
          ('MMC-SIR', 'Recognises the speaker against the gallery of enrolled Users and gives the Instance Identifier of the User.', [dt('OSD-BSO', 'Speech Object')], [dt('OSD-IID', 'Instance Identifier') + ' (from speech)']),
          ('OSD-IDR', 'Reconciles the two Instance Identifiers and issues the Response and its Personal Status.', [dt('OSD-IID', 'Instance Identifier') + ' (from face)', dt('OSD-IID', 'Instance Identifier') + ' (from speech)'], [dt('OSD-BTO', 'Response'), dt('MMC-EPS', 'Personal Status')]),
          ('PAF-RSR', 'Receives the Response, the Prompt, the Personal Status, the Avatar Model and the Machine ID and produces the Portable Avatar that speaks them with lip-synchronised Machine Face Descriptors.',
           [dt('OSD-BTO', 'Response'), dt('OSD-BTO', 'Prompt'), dt('MMC-EPS', 'Personal Status'), dt('OSD-B3O', 'Avatar Model'), dt('string', 'Machine ID')], [dt('PAF-PAV', 'Portable Avatar')])],
    fig1=(['Face Object', 'Speech Object', 'Prompt', 'Avatar Model', 'Machine ID'], ['Response', 'Identification', 'Portable Avatar']),
    fig2=dict(w=1180, h=440,
              boxes=[('FIR', 190, 50, 150, 90, ['Face Identity', 'Recognition']), ('SIR', 190, 175, 150, 90, ['Speaker Identity', 'Recognition']), ('IDR', 480, 100, 150, 130, ['ID', 'Reconciliation']),
                     ('RSR', 880, 130, 170, 130, ['Response and', 'Scene Rendering'])],
              arrows=[([(20, 95), (190, 95)], ['Face Object'], 105, 81), ([(20, 220), (190, 220)], ['Speech Object'], 105, 206), ([(340, 95), (410, 95), (410, 140), (480, 140)], ['Instance', 'Identifier'], 400, 65),
                      ([(340, 220), (410, 220), (410, 190), (480, 190)], ['Instance', 'Identifier'], 400, 240), ([(630, 150), (880, 150)], ['Response'], 755, 136), ([(630, 195), (880, 195)], ['Personal Status'], 755, 181),
                      ([(755, 150), (755, 60), (1170, 60)], ['Response'], 960, 46), ([(20, 270), (880, 270)], ['Prompt'], 150, 256), ([(20, 330), (940, 330), (940, 260)], ['Avatar Model'], 120, 316),
                      ([(20, 375), (1000, 375), (1000, 260)], ['Machine ID'], 120, 361), ([(1050, 195), (1170, 195)], ['Portable', 'Avatar'], 1110, 172), ([(560, 230), (560, 410), (1170, 410)], ['Identification'], 860, 396)]))

NPR = Spec(
    acr='MMC-NPR', name='Newspaper Reader', slug='newspaper-reader', schema='NewspaperReading.json',
    function=('The Newspaper Reader (MMC‑NPR) AIM reads a column of a newspaper to a person. It receives the image of a newspaper page and, when the person has pointed at the top of a column, the Point; it returns the text of that '
              'column as a Portable Avatar that reads it.'),
    steps=['A human provides:\n<ol type="a">\n \t<li>The image of a newspaper page.</li>\n \t<li>When the human points at the top of a column, the Point.</li>\n</ol>',
           'The User Agent provides the Machine ID.',
           'The machine provides the Portable Avatar that reads the text of the column.'],
    inputs=[('Input Visual', 'OSD-BVO', 'The image of the newspaper page.'), ('Input Point', 'OSD-PNT', 'The Point at the top of the column that the person has pointed at. Optional.'),
            ('Machine ID', 'string', 'Identifier of the machine.')],
    outputs=[('Portable Avatar', 'PAF-PAV', 'The avatar that reads the text of the column.')],
    operation=('The Newspaper Reader (MMC‑NPR) AIM receives the image of the newspaper page and, if the person has pointed at the top of a column, the Point. Optical Character Recognition finds the lines of text of a band of the page '
               'about the Point. Column Text Selection takes the text of the column from those lines. Portable Avatar Multiplexing puts the text in the Portable Avatar, which reads it. Acquisition and delivery are the User Agent’s, not SubAIMs.'),
    subs=[('MMC-OCR', 'Finds the lines of text of a band of the page image about the Point.', [dt('OSD-BVO', 'Visual'), dt('OSD-PNT', 'Point')], ['Text lines (Text Object)']),
          ('MMC-CTS', 'Takes the text of the column from the lines found, given the Point.', ['Text lines (Text Object)', dt('OSD-PNT', 'Point')], [dt('OSD-BTO', 'Text') + ' of the column']),
          ('PAF-PMX', 'Puts the text of the column and the Machine ID in the Portable Avatar.', [dt('OSD-BTO', 'Text') + ' of the column', dt('string', 'Machine ID')], [dt('PAF-PAV', 'Portable Avatar')])],
    fig1=(['Input Visual', 'Input Point', 'Machine ID'], ['Portable Avatar']),
    fig2=dict(w=1060, h=380,
              boxes=[('OCR', 190, 60, 150, 100, ['Optical', 'Character', 'Recognition']), ('CTS', 470, 60, 150, 100, ['Column Text', 'Selection']), ('PMX', 760, 90, 170, 120, ['Portable Avatar', 'Multiplexing'])],
              arrows=[([(20, 100), (190, 100)], ['Input Visual'], 105, 86), ([(20, 145), (190, 145)], None, 0, 0), ([(340, 110), (470, 110)], ['Text lines'], 405, 96), ([(20, 145), (110, 145), (110, 230), (545, 230), (545, 160)], ['Input Point'], 250, 216),
                      ([(620, 110), (700, 110), (700, 125), (760, 125)], ['Column Text'], 690, 96), ([(20, 300), (845, 300), (845, 210)], ['Machine ID'], 120, 286), ([(930, 150), (1050, 150)], ['Portable', 'Avatar'], 990, 127)]))

ALL = [MCA, MTS, AMC, ACR, MAC, NPR]

if __name__ == '__main__':
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    for s in ALL:
        open(os.path.join(out, s.slug + '.html'), 'w', encoding='utf8').write(page(s))
        open(os.path.join(out, s.slug + '-1.svg'), 'w', encoding='utf8').write(fig_aim(s.name, s.acr, *s.fig1))
        f2 = s.fig2
        open(os.path.join(out, s.slug + '-2.svg'), 'w', encoding='utf8').write(fig_composite(f2['w'], f2['h'], f2['boxes'], f2['arrows']))
    print('wrote', sorted(os.listdir(out)))
