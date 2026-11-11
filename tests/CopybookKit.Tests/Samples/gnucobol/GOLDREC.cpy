      *---------------------------------------------------------------
      * GOLDREC - every numeric format the decoder supports.
      * Odd and even digit counts differ in COMP-3: even counts leave
      * one unused high nibble.
      *---------------------------------------------------------------
       01  GOLD-REC.
           05  G-CASE                  PIC 9(04).
           05  G-PACK-ODD              PIC S9(11)V99 COMP-3.
           05  G-PACK-EVEN             PIC S9(10)V99 COMP-3.
           05  G-PACK-UNS              PIC 9(05) COMP-3.
           05  G-PACK-MAX              PIC S9(18) COMP-3.
           05  G-DISP-OVER             PIC S9(05)V99.
           05  G-DISP-LEAD             PIC S9(05)V99
                                       SIGN IS LEADING SEPARATE.
           05  G-DISP-TRAIL            PIC S9(05)V99
                                       SIGN IS TRAILING SEPARATE.
           05  G-BIN-4                 PIC S9(04) COMP.
           05  G-BIN-9                 PIC S9(09) COMP.
           05  G-BIN-18                PIC S9(18) COMP.
