import { Component, OnInit, Inject, ViewChild, AfterViewInit, ElementRef } from '@angular/core';
import { MAT_DIALOG_DATA, MatDialog } from '@angular/material/dialog';
import { DevOpsServiceClient } from '../service-client.service';
import { MatSelectionList } from '@angular/material/list';
import * as moment from 'moment-timezone';
import { EnterPassphraseComponent } from '../upload-certificate/enter-passphrase/enter-passphrase.component';
import { Observable, of } from 'rxjs';
import { map, switchMap, tap, catchError, finalize } from 'rxjs/operators';
import { MatSnackBar } from '@angular/material/snack-bar';

@Component({
  selector: 'app-edit-trusted-certificates',
  templateUrl: './edit-trusted-certificates.component.html',
  styleUrls: ['./edit-trusted-certificates.component.scss']
})
export class EditTrustedCertificatesComponent implements OnInit, AfterViewInit {

  trustedCertificates: any[];
  useSsl: boolean;
  selectedCert: any;
  localizedValidFrom: string;
  isLoading: boolean;
  showExplanatoryText: boolean;
  error = null;

  @ViewChild('certificates', { static: false }) certList: MatSelectionList;
  @ViewChild('fileSelectInputDialog', { static: false }) fileSelectInputDialog: ElementRef;

  constructor(
    @Inject(MAT_DIALOG_DATA) public data: any,
    private serviceClient: DevOpsServiceClient,
    private dialog: MatDialog,
    private snackbar: MatSnackBar
  ) { }

  ngOnInit(): void {
    this.trustedCertificates = this.data?.trustedCertificates ?? [];

    this.serviceClient.getSafeguard().subscribe((data: any) => {
      if (data) {
        this.useSsl = !data.IgnoreSsl;
      }
    });
  }

  ngAfterViewInit(): void {
    this.certList.selectionChange.subscribe((x) => {
      if (!this.selectedCert) {
        this.selectedCert = x.options[0].value;
        this.localizedValidFrom = moment(this.selectedCert.NotBefore).format('LLL (Z)') + ' - ' + moment(this.selectedCert.NotAfter).format('LLL (Z)');
      }
    });
  }

  browse(): void {
    const e: HTMLElement = this.fileSelectInputDialog.nativeElement;
    e.click();
  }

  updateUseSsl(): void {
    this.error = null;
    this.serviceClient.putSafeguardUseSsl(this.useSsl)
      .subscribe(() => { },
        error => {
          this.useSsl = false;
          this.error = error;
        });
  }

  onChangeFile(files: FileList): void {
    if (!files[0]) {
      return;
    }

    const fileSelected = files[0];

    const fileReader = new FileReader();
    fileReader.onloadend = (e) => {
      const arrayBufferToString = (buffer) => {
        let binary = '';
        const bytes = new Uint8Array(buffer);
        const len = bytes.byteLength;
        for (let i = 0; i < len; i++) {
          binary += String.fromCharCode(bytes[i]);
        }
        return binary;
      };

      const pkcs12Der = arrayBufferToString(fileReader.result);
      const cert: string = btoa(pkcs12Der);

      const fileData = {
        fileType: fileSelected.type,
        fileContents: cert,
        fileName: fileSelected.name
      };

      let isNew = false;
      this.getPassphrase(fileData).pipe(
        tap(() => {
          this.isLoading = true;
          this.trustedCertificates.splice(0);
        }),
        switchMap((data) => this.saveCertificate(data)),
        switchMap((data) => {
          isNew = data[0]?.IsNew;
          return this.refreshCertificates();
        }),
        finalize(() => {
          this.isLoading = false;

          // Clear the selection
          const input = this.fileSelectInputDialog.nativeElement as HTMLInputElement;
          input.value = null;
        })
      ).subscribe(() => {
        if (isNew) {
          this.snackbar.open(`Added certificate ${fileData.fileName}`, 'Dismiss', { duration: 5000 });
        } else {
          this.snackbar.open(`Certificate ${fileData.fileName} already exists.`, 'Dismiss', { duration: 5000 });
        }
      });
    };
    fileReader.readAsArrayBuffer(fileSelected);
  }

  private getPassphrase(fileData: any): Observable<any[]> {
    if (fileData?.fileType !== 'application/x-pkcs12') {
      return of([fileData]);
    }

    const ref = this.dialog.open(EnterPassphraseComponent, {
      data: { fileName: fileData.fileName }
    });

    return ref.afterClosed().pipe(
      // Emit fileData as well as passphrase
      // if passphraseData == undefined then they canceled the dialog
      map(passphraseData => (!passphraseData) ? [] : [fileData, passphraseData])
    );
  }

  private saveCertificate(resultArray: any[]): Observable<any> {
    const fileContents = resultArray[0]?.fileContents;
    if (!fileContents) {
      return of([]);
    }

    const passphrase = resultArray.length > 1 ? resultArray[1] : '';
    return this.serviceClient.postTrustedCertificates(false, fileContents, passphrase).pipe(
      catchError((err) => {
        if (err.error?.Message?.includes('specified network password is not correct')) {
          // bad password, have another try?
          // it's all we get
          this.snackbar.open(`The password for the certificate in ${resultArray[0].fileName} was not correct.`,
            'Dismiss', { duration: 5000 });
        } else if (err.error?.Message) {
          this.snackbar.open(err.error.Message, 'Dismiss', { duration: 5000 });
        }
        return of([]);
      })
    );
  }

  private refreshCertificates(): Observable<any[]> {
    return this.serviceClient.getTrustedCertificates().pipe(
      tap((certs) => {
        this.trustedCertificates.splice(0);
        this.trustedCertificates.push(...certs);
      }));
  }

  import(): void {
    this.isLoading = true;
    this.trustedCertificates.splice(0);
    let newTrustedCertsCount = 0;
    let existingTrustedCertsCount = 0;
    this.serviceClient.postTrustedCertificates(true).pipe(
      switchMap((trustedCerts) => {
        newTrustedCertsCount = trustedCerts.filter(cert => cert.IsNew).length;
        existingTrustedCertsCount = trustedCerts.filter(cert => !cert.IsNew).length;
        return this.refreshCertificates();
      })
    ).subscribe(() => {
      this.isLoading = false;
      if (newTrustedCertsCount > 0 && existingTrustedCertsCount == 0) {
        this.snackbar.open(`Imported ${newTrustedCertsCount} certificates.`, 'Dismiss', { duration: 5000 });
      } else {
        this.snackbar.open(`Imported ${newTrustedCertsCount} new certificates and ${existingTrustedCertsCount} existing certificates.`, 'Dismiss', { duration: 5000 });
      }
    });
  }

  removeCertificate(): void {
    this.error = null;
    const thumbprint = this.selectedCert.Thumbprint;
    this.selectedCert = null;
    this.isLoading = true;
    this.serviceClient.deleteTrustedCertificate(thumbprint).pipe(
      switchMap(() => this.refreshCertificates())
    ).subscribe(() => {
      if (this.trustedCertificates.length == 0) {
        this.useSsl = false;
        this.updateUseSsl();
      }
      this.isLoading = false;
      },
      error => {
        this.isLoading = false;
        this.error = error;
      });
  }

  closeCertDetails(): void {
    this.selectedCert = null;
  }
}
